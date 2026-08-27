<#
  Vivarium Phase 0 spike: Hyper-V Standard-checkpoint revert latency (ARCHITECTURE D5).

  Creates N throwaway Gen2 VMs (no OS; differencing disks off one parent — the sealed-parent ->
  pool-VM shape of D5/8.3), takes a Standard checkpoint of each *running* VM, then measures the
  pool revert cycle:  TurnOff -> Restore-VMCheckpoint -> Start (resume from saved memory)
  for a single VM and for all N concurrently. Also records what Production / ProductionOnly
  checkpoints do on such a VM (the D5 pin rationale). Cleans up after itself unless -KeepVms.

  Results land in docs/spikes/hyperv-checkpoints.md.
#>
param(
    [int]$VmCount = 5,
    [int]$MemoryGB = 2,
    [string]$WorkDir = 'D:\VivariumSpike.tmp',
    [int]$SingleCycles = 3,
    [switch]$KeepVms
)

$ErrorActionPreference = 'Stop'
Import-Module Hyper-V

$prefix = 'vivspike-'
$names = @(1..$VmCount | ForEach-Object { "$prefix$_" })

function Remove-SpikeVms {
    foreach ($vm in Get-VM | Where-Object Name -like "$prefix*") {
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction SilentlyContinue }
        Remove-VM -VM $vm -Force -Confirm:$false
    }
}

function Measure-Ms([scriptblock]$Block) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Block | Out-Null
    $sw.Stop()
    [math]::Round($sw.Elapsed.TotalMilliseconds)
}

function Invoke-Parallel([string[]]$VmNames, [string]$Verb) {
    $jobs = foreach ($n in $VmNames) {
        switch ($Verb) {
            'turnoff' { Stop-VM -Name $n -TurnOff -Force -Confirm:$false -AsJob }
            'apply'   { Restore-VMCheckpoint -VMName $n -Name pristine -Confirm:$false -AsJob }
            'start'   { Start-VM -Name $n -AsJob }
        }
    }
    Wait-Job $jobs | Out-Null
    $failed = $jobs | Where-Object State -ne 'Completed'
    Remove-Job $jobs -Force
    if ($failed) { throw "parallel '$Verb' had $($failed.Count) failed job(s)" }
}

Write-Output "=== Vivarium Hyper-V checkpoint spike: $VmCount VM(s) x ${MemoryGB}GB static ==="
Remove-SpikeVms
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDir | Out-Null

$parent = Join-Path $WorkDir 'parent.vhdx'
New-VHD -Path $parent -SizeBytes 10GB -Dynamic | Out-Null
$createMs = Measure-Ms {
    foreach ($name in $names) {
        $disk = Join-Path $WorkDir "$name.vhdx"
        New-VHD -Path $disk -ParentPath $parent -Differencing | Out-Null
        $vm = New-VM -Name $name -MemoryStartupBytes ([int64]$MemoryGB * 1GB) -Generation 2 -Path $WorkDir -VHDPath $disk
        Set-VM -VM $vm -StaticMemory -CheckpointType Standard -AutomaticCheckpointsEnabled $false
        Set-VMFirmware -VMName $name -EnableSecureBoot Off
        Get-VMNetworkAdapter -VMName $name | Remove-VMNetworkAdapter
    }
}
Write-Output ("create {0} VM(s) + diff disks: {1} ms" -f $VmCount, $createMs)

$startMs = Measure-Ms { Invoke-Parallel $names 'start' }
Write-Output ("first cold Start of all: {0} ms" -f $startMs)
Start-Sleep -Seconds 5   # let firmware settle at 'no boot device'

foreach ($name in $names) {
    $ms = Measure-Ms { Checkpoint-VM -Name $name -SnapshotName pristine }
    Write-Output ("checkpoint (CHECKPOINTING) {0}: {1} ms" -f $name, $ms)
}
foreach ($f in Get-ChildItem $WorkDir -Recurse -Filter *.vmrs | Sort-Object Length -Descending) {
    Write-Output ("vmrs: {0}  {1:N1} MB" -f $f.Name, ($f.Length / 1MB))
}

$one = $names[0]
for ($i = 1; $i -le $SingleCycles; $i++) {
    $off = Measure-Ms { Stop-VM -Name $one -TurnOff -Force -Confirm:$false }
    $apply = Measure-Ms { Restore-VMCheckpoint -VMName $one -Name pristine -Confirm:$false }
    $stateAfterApply = (Get-VM -Name $one).State
    $resume = Measure-Ms { Start-VM -Name $one }
    $stateAfterStart = (Get-VM -Name $one).State
    Write-Output ("single cycle {0} [{1}]: turnoff {2} ms + apply {3} ms (-> {4}) + resume {5} ms (-> {6}) = TOTAL {7} ms" -f `
        $i, $one, $off, $apply, $stateAfterApply, $resume, $stateAfterStart, ($off + $apply + $resume))
}

if ($VmCount -gt 1) {
    $offMs = Measure-Ms { Invoke-Parallel $names 'turnoff' }
    $applyMs = Measure-Ms { Invoke-Parallel $names 'apply' }
    $resumeMs = Measure-Ms { Invoke-Parallel $names 'start' }
    $states = ($names | ForEach-Object { (Get-VM -Name $_).State }) -join ','
    Write-Output ("concurrent x{0}: turnoff {1} ms + apply {2} ms + resume {3} ms = TOTAL {4} ms (states: {5})" -f `
        $VmCount, $offMs, $applyMs, $resumeMs, ($offMs + $applyMs + $resumeMs), $states)
}

try {
    Set-VM -Name $one -CheckpointType Production
    $ms = Measure-Ms { Checkpoint-VM -Name $one -SnapshotName prod-test }
    $cp = Get-VMCheckpoint -VMName $one -Name prod-test
    Write-Output ("Production checkpoint: created in {0} ms, reported type: {1}" -f $ms, $cp.SnapshotType)
}
catch {
    Write-Output ("Production checkpoint FAILED: {0}" -f $_.Exception.Message)
}
try {
    Set-VM -Name $one -CheckpointType ProductionOnly
    $ms = Measure-Ms { Checkpoint-VM -Name $one -SnapshotName prodonly-test }
    Write-Output ("ProductionOnly checkpoint: created in {0} ms (unexpected on an OS-less VM)" -f $ms)
}
catch {
    Write-Output ("ProductionOnly checkpoint FAILED (expected without guest VSS): {0}" -f $_.Exception.Message)
}

if (-not $KeepVms) {
    Remove-SpikeVms
    Remove-Item $WorkDir -Recurse -Force
    Write-Output 'cleaned up'
}
Write-Output '=== spike done ==='
