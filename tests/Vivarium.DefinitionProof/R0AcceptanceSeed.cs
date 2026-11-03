using System.Text;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

internal static class R0AcceptanceSeed
{
    internal const string RepositoryId = "r0-acceptance";
    internal const string LogScript = """
        $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        [Console]::Out.Write("owned-log-stdout`n")
        [Console]::Out.Flush()
        [Console]::Error.Write("owned-log-stderr`n")
        [Console]::Error.Flush()
        [IO.File]::WriteAllText([IO.Path]::Combine($env:CASE_DIR,'entered'),'entered')
        $end=[DateTime]::UtcNow.AddSeconds(60)
        while (!(Test-Path -LiteralPath ([IO.Path]::Combine($env:CASE_DIR,'release')))) {
          if ([DateTime]::UtcNow -gt $end) { exit 91 }
          Start-Sleep -Milliseconds 50
        }
        [Console]::Out.Write("owned-log-tail`n")
        [Console]::Out.Flush()
        """;
    private const string SecretScript = """
        $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $value=$env:OWNED_SECRET
        $half=[int][Math]::Floor($value.Length/2)
        [Console]::Out.Write('left-'+$value.Substring(0,$half)); [Console]::Out.Flush()
        Start-Sleep -Milliseconds 250
        [Console]::Out.Write($value.Substring($half)+"-right`n"); [Console]::Out.Flush()
        [Console]::Error.Write('error-'+$value+"-end`n"); [Console]::Error.Flush()
        """;
    private const string BulkScript = """
        $ProgressPreference='SilentlyContinue'
        $out=[Console]::OpenStandardOutput(); $err=[Console]::OpenStandardError()
        $a=[Text.Encoding]::ASCII.GetBytes(('A'*128)); $b=[Text.Encoding]::ASCII.GetBytes(('B'*128))
        $end=[DateTime]::UtcNow.AddSeconds(110)
        for ($i=0; $i -lt 128; $i++) {
          $out.Write($a,0,$a.Length); $err.Write($b,0,$b.Length); $out.Flush(); $err.Flush()
          while (!(Test-Path -LiteralPath ([IO.Path]::Combine($env:CASE_DIR,('continue-'+$i))))) {
            if ([DateTime]::UtcNow -gt $end) { exit 91 }; Start-Sleep -Milliseconds 10
          }
        }
        """;

    private static string PowerShell(string script) => "powershell -NoProfile -InputFormat Text -OutputFormat Text -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    internal static async Task CreateAsync(string root)
    {
        Directory.CreateDirectory(root);
        await SourceCompiler.GitAsync(root, "init", "-b", "main");
        await SourceCompiler.GitAsync(root, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(root, "config", "user.name", "Owned R0 acceptance");
        await SourceCompiler.GitAsync(root, "config", "user.email", "fixture@example.invalid");
        var source = ProductionEvaluatorPhysicalTests.Sources();
        var smoke = """
            $ProgressPreference='SilentlyContinue'
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            if ($env:MODE -eq 'fail') { exit 7 }
            if ($env:MODE -eq 'long') {
              [Console]::Out.Write("owned-running-start`n"); [Console]::Out.Flush()
              $end=[DateTime]::UtcNow.AddSeconds(60)
              while (!(Test-Path -LiteralPath ([IO.Path]::Combine($env:CASE_DIR,'release')))) {
                if ([DateTime]::UtcNow -gt $end) { exit 91 }
                Start-Sleep -Milliseconds 50
              }
            }
            [IO.File]::WriteAllText('smoke.txt',"owned-r0-smoke`n",[Text.UTF8Encoding]::new($false))
            [Console]::Out.Write("owned-r0-stdout`n")
            [Console]::Out.WriteLine("##teamcity[publishArtifacts 'smoke.txt']")
            """;
        var settings = source["Settings.g.cs"].Replace("\"echo from CSharp Git\"", JsonSerializer.Serialize(PowerShell(smoke)), StringComparison.Ordinal)
            .Replace("\"Echo\"", "\"Owned byte producer\"", StringComparison.Ordinal)
            .Replace("new(\"P_LocalSmoke\",", $$"""
                new("P_Log", "Literal logs", [], [new("logs", "Logs", "simpleRunner", [new("script.content", {{JsonSerializer.Serialize(PowerShell(LogScript))}}, null)])], [], [], [], 2, []),
                new("P_Empty", "Empty output", [], [new("empty", "Empty", "simpleRunner", [new("script.content", "exit /b 0", null)])], [], [], [], 1, []),
                new("P_Secret", "Registered secret output", [], [new("secret", "Secret", "simpleRunner", [new("script.content", {{JsonSerializer.Serialize(PowerShell(SecretScript))}}, null)])], [], [], [], 1, []),
                new("P_Bulk", "Bounded paging output", [], [new("bulk", "Bulk", "simpleRunner", [new("script.content", {{JsonSerializer.Serialize(PowerShell(BulkScript))}}, null)])], [], [], [], 2, []),
                new("P_LocalSmoke",
                """, StringComparison.Ordinal);
        var files = new Dictionary<string, string>
        {
            [".vivarium/repository.yaml"] = Vivarium.Controller.Catalog.DefinitionGitReader.Manifest(RepositoryId, control: true) + "\n",
            [".vivarium/projects/P/Control.g.cs"] = source["Control.g.cs"],
            [".vivarium/unbound/P/Settings.g.cs"] = settings,
            [".vivarium/unbound/P/UserCode.cs"] = source["UserCode.cs"],
            [".vivarium/projects/Q/Control.g.cs"] = source["Control.g.cs"].Replace("\"P\"", "\"Q\"").Replace("Project P", "Project Q"),
            [".vivarium/unbound/Q/Settings.g.cs"] = settings.Replace("\"P\"", "\"Q\"").Replace("P_", "Q_"),
            [".vivarium/unbound/Q/UserCode.cs"] = source["UserCode.cs"],
        };
        foreach (var (path, text) in files)
        {
            var file = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, text);
        }
        await SourceCompiler.GitAsync(root, "add", "--all");
        await SourceCompiler.GitAsync(root, "commit", "-m", "fixture: immutable R0 source seed");
    }
}
