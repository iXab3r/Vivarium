using DefinitionProof;
public static class Managed
{
    public static Settings Declare() => new("T3Personal", new("T3Personal_Git", "https://github.com/iXab3r/t3code.git", "refs/heads/main", "+:.\n-:.repos"),
        [new("env.MODE", "base", null)],
        [
            new("T3Personal_Prepare", "Prepare", [new("env.MODE", null, "fixture-mode")], [new("Prepare_step", "Prepare", "simpleRunner", [new("script.content", "bash scripts/personal-release/prepare.sh %build.counter%", null)])], [new("teamcity.agent.name", "Cvat")], [], [], 90, []),
            new("T3Personal_Windows", "Windows", [], [new("Windows_step", "Windows", "jetbrains_powershell", [new("jetbrains_powershell_script_file", "scripts/personal-release/windows.ps1", null)])], [new("teamcity.agent.name", "laptop-g15")], [new("T3Personal_Prepare", "CANCEL", "CANCEL")], [new("T3Personal_Prepare", "sameChainOrLastFinished", "prepare/** => ci-input/prepare", true)], 90, []),
            new("T3Personal_Release", "Release", [new("env.T3_PUBLISH", "release", null)], [new("Release_step", "Release", "simpleRunner", [new("script.content", "node scripts/personal-release/run.mjs stage-release\nbash scripts/personal-release/publish.sh", null)])], [new("teamcity.agent.name", "Cvat")], [new("T3Personal_Windows", "CANCEL", "CANCEL")], [new("T3Personal_Windows", "sameChainOrLastFinished", "windows/** => ci-input/windows", true), new("T3Personal_Prepare", "sameChainOrLastFinished", "linux/** => ci-input/linux", true)], 15, [new("vcs", "+:<default>", 60)])
        ]);
}
