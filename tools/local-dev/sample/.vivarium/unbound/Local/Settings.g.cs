using Vivarium.Definitions;
public static class Managed {
    public static Settings Declare() => new("Local", null, [], [
        new("Local_Success", "Success — log and file artifact", [], [
            new("success", "Write proof.txt and publish it", "simpleRunner", [
                new("script.content", "echo Hello from the local Vivarium Agent\necho local-development-proof>proof.txt\necho ##teamcity[publishArtifacts 'proof.txt']", null)
            ])
        ], [], [], [], 1, []),
        new("Local_Failure", "Failure — controlled exit 7", [], [
            new("failure", "Explain the intentional failure", "simpleRunner", [new("script.content", "echo This example intentionally exits with code 7\nexit 7", null)])
        ], [], [], [], 1, []),
        new("Local_Cancel", "Cancel me — stop a running Build", [], [
            new("wait", "Wait for operator cancellation", "simpleRunner", [new("script.content", "echo Cancel this Build from Queue and Builds\nping -n 21 127.0.0.1 >nul", null)])
        ], [], [], [], 10, [])
    ]);
}
