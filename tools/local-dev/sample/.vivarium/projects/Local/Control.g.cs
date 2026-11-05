using Vivarium.Definitions;
public static class Control {
    public static Project Declare() => new("Local", "_Root", "Local development examples",
        "Run Success for a real log and proof.txt artifact, Failure for exit 7, or Cancel me to practice stopping a running Build. Inspect configuration steps and build history. Sources live in artifacts/local-dev/lab/definitions.", null);
}
