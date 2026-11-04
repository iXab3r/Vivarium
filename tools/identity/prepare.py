"""Snapshot only selected sources and hash-verified dependencies; never include local runtime/private state."""
from pathlib import Path
import hashlib
import json
import subprocess
import uuid

root = Path(__file__).resolve().parents[2]
context = root / "artifacts" / "r0-host" / ("identity-context-" + uuid.uuid4().hex)
context.mkdir(parents=True)
inputs = []


def copy(source, relative, expected=None):
    data = source.read_bytes()
    sha = hashlib.sha256(data).hexdigest()
    if expected is not None and sha != expected:
        raise RuntimeError("Dependency hash differs: " + str(relative))
    target = context / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    inputs.append({"path": str(relative).replace("\\", "/"), "sha256": sha, "bytes": len(data)})


prefixes = ("src/Vivarium.Controller/", "src/Vivarium.Contracts/", "tests/Vivarium.Acceptance/", "tools/Vivarium.Automation/")
selected = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode().split("\0")
for path in sorted(set(selected)):
    if path in ("Directory.Build.props", "NuGet.Config", "automation.json") or path.startswith(prefixes):
        copy(root / path, Path(path))
module = root / "submodules/EyeAuras.Automation"
pin = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=module).decode().strip()
if pin != "c09facd10b413e6a3ebfd117379009e11c00b4b4":
    raise RuntimeError("Automation pin differs")
if subprocess.run(["git", "diff", "--quiet", "HEAD"], cwd=module).returncode:
    raise RuntimeError("Pinned Automation sources have local changes")
for path in subprocess.check_output(["git", "ls-files", "-z"], cwd=module).decode().split("\0"):
    if path == "Directory.Build.props" or path == "README.md" or path.startswith((
            "src/EyeAuras.Automation/", "src/EyeAuras.Automation.Cli/", "src/EyeAuras.Automation.Playwright/")):
        copy(module / path, Path("submodules/EyeAuras.Automation") / path)
for release in json.loads((root / "dependency-artifacts.json").read_text(encoding="utf-8"))["releases"]:
    for file in release["files"]:
        path = Path("artifacts/dependencies/nuget") / file["name"]
        copy(root / path, path, file["sha256"])
copy(root / "artifacts/r0-dependencies/oidc/eyeauras-oidc-fixture-0.1.0-depot.4-linux-arm64.tar.gz",
     Path("fixtures/issuer.tar.gz"), "df1aea615428eac8742cb30d52b4dd7e76c713712d7e8089d8cd4b3a2997234f")
copy(root / "tools/identity/Dockerfile", Path("Dockerfile"))
(context / "inputs.json").write_text(json.dumps({"automation_pin": pin,
    "runtime_platform": "linux/amd64", "issuer_distribution": "linux-arm64 (unchanged managed entry)",
    "inputs": inputs}, indent=2), encoding="utf-8")
print(context)
