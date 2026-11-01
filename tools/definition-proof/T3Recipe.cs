using DefinitionProof;

// Literal translation of explicit declarations at t3code 7ff65cada80dd817aa5d7467b85e78ec30c578a9.
// This trusted source is compiled as an alternative SettingsSource, never executed as a workload.
public static class Managed
{
    private const string Image = "node:26.12.1-bookworm@sha256:3b764117159bda01d2396391e7789b6a9106af9729cfd001ef55e98557868310";
    public static Settings Declare() => new("T3Personal",
        new("T3Personal_Git", "https://github.com/iXab3r/t3code.git", "refs/heads/main", "+:.\n-:.repos", "T3 personal integration"),
        [],
        [
            new("T3Personal_Prepare", "Prepare / Linux x64 desktop and runtime", [],
                [new("Prepare_step", "Build shared bundle, Linux desktop and WSL runtime", "simpleRunner",
                    [new("script.content", "#!/usr/bin/env bash\nmkdir -p release\nset +e\nbash scripts/personal-release/prepare.sh %build.counter% 2>&1 | tee release/prepare.log\nresult=${PIPESTATUS[0]}\necho \"##teamcity[publishArtifacts 'release/prepare.log => diagnostics']\"\nexit \"$result\"", null),
                     new("plugin.docker.imageId", Image, null),
                     new("plugin.docker.imagePlatform", "linux", null),
                     new("plugin.docker.run.parameters", "--user 0 --init --platform linux/amd64 --volume t3-personal-vite:/root/.vite-plus --volume t3-personal-cargo:/root/.cargo --volume t3-personal-rustup:/root/.rustup", null)])],
                [new("teamcity.agent.name", "Cvat")], [], [], 90, [], "ON_AGENT"),
            new("T3Personal_Windows", "Windows x64 / installer and launch check", [],
                [new("Windows_step", "Package and verify Windows desktop", "jetbrains_powershell",
                    [new("jetbrains_powershell_script_mode", "FILE", null),
                     new("jetbrains_powershell_script_file", "scripts/personal-release/windows.ps1", null),
                     new("jetbrains_powershell_execution", "PS1", null),
                     new("jetbrains_powershell_noprofile", "true", null),
                     new("jetbrains_powershell_bitness", "x64", null)])],
                [new("teamcity.agent.name", "laptop-g15")],
                [new("T3Personal_Prepare", "CANCEL", "CANCEL")],
                [new("T3Personal_Prepare", "sameChainOrLastFinished", "prepare/** => ci-input/prepare", true)], 90, [], "ON_AGENT"),
            new("T3Personal_Release", "Release / validate and publish", [new("env.T3_PUBLISH", "release", null)],
                [new("Release_step", "Validate complete release assets", "simpleRunner",
                    [new("script.content", "#!/usr/bin/env bash\nset -euo pipefail\nnode scripts/personal-release/run.mjs stage-release\nbash scripts/personal-release/publish.sh", null),
                     new("plugin.docker.imageId", Image, null),
                     new("plugin.docker.imagePlatform", "linux", null),
                     new("plugin.docker.run.parameters", "--user 0 --init", null)])],
                [new("teamcity.agent.name", "Cvat")],
                [new("T3Personal_Windows", "CANCEL", "CANCEL")],
                [new("T3Personal_Windows", "sameChainOrLastFinished", "windows/** => ci-input/windows", true),
                 new("T3Personal_Prepare", "sameChainOrLastFinished", "linux/** => ci-input/linux", true)],
                15, [new("vcs", "+:<default>", 60, "USE_CUSTOM")], "ON_AGENT")
        ]);
}
