"""Build a frozen identity contour, execute it with no external network, and export bounded result evidence only."""
from pathlib import Path
import hashlib
import json
import re
import subprocess
import sys
import tarfile
import time
import uuid
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
context = Path(sys.argv[1]).resolve()
if context.parent != root / "artifacts/r0-host" or not re.fullmatch(r"identity-context-[a-f0-9]{32}", context.name):
    raise RuntimeError("Select an owned identity build context")
run_id = uuid.uuid4().hex
results = root / "artifacts/r0-host" / ("identity-run-" + run_id)
results.mkdir()
name = "vivarium-us013-identity-" + run_id
receipt = {"id": run_id, "context": str(context), "started": time.time(), "outcome": "SETUP_FAILED",
           "producer_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
           "runtime_platform": "linux/amd64", "issuer_distribution": "linux-arm64 (unchanged managed entry)"}
(results / "producer.py").write_bytes(Path(__file__).read_bytes())
container_id = None
image = None


def command(args, timeout=120):
    return subprocess.check_output(["docker", *args], text=True, encoding="utf-8", timeout=timeout).strip()


def inspect_owned():
    value = json.loads(command(["inspect", container_id]))[0]
    if value["Id"] != container_id or value["Image"] != image or value["Config"]["Labels"].get("vivarium.identity.run") != run_id:
        raise RuntimeError("Container ownership changed")
    return value


try:
    with (results / "build.log").open("wb") as log:
        built = subprocess.run(["docker", "build", "--platform", "linux/amd64", "--iidfile", str(context / "image-id"), str(context)],
                               stdout=log, stderr=subprocess.STDOUT, timeout=1800)
    receipt["build_exit"] = built.returncode
    if built.returncode:
        raise RuntimeError("Identity contour preparation failed; inspect build.log")
    image = (context / "image-id").read_text().strip()
    if not re.fullmatch("sha256:[a-f0-9]{64}", image):
        raise RuntimeError("Prepared image identity is invalid")
    receipt["image"] = image
    container_id = command(["create", "--name", name, "--label", "vivarium.identity.run=" + run_id,
                            "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                            "--cpus", "2", "--memory", "3g", "--memory-swap", "3g", "--pids-limit", "768",
                            "--shm-size", "256m", image])
    receipt["container_id"] = container_id
    value = inspect_owned()
    receipt["runtime_command"] = value["Config"]["Cmd"]
    host = value["HostConfig"]
    if (host["NetworkMode"] != "none" or host["Privileged"] or value["Config"]["User"] != "vivarium"
            or value["Mounts"] or host.get("PortBindings") or host["CapDrop"] != ["ALL"]
            or "no-new-privileges" not in host["SecurityOpt"] or host["Memory"] != 3 * 1024**3
            or host["NanoCpus"] != 2_000_000_000 or host["PidsLimit"] != 768):
        raise RuntimeError("Identity contour isolation differs")
    receipt["isolation"] = {"network": "none", "mounts": 0, "published_ports": 0, "user": "vivarium",
                            "memory_bytes": host["Memory"], "nano_cpus": host["NanoCpus"], "pids": host["PidsLimit"]}
    command(["start", container_id])
    exit_code = int(command(["wait", container_id], timeout=720))
    receipt["runtime_exit"] = exit_code
    # stdout/stderr and working directories remain private to this disposable container.
    # Read only selected summaries/TRX from a tar stream, never extract its paths or private state.
    selected = {}
    with subprocess.Popen(["docker", "cp", container_id + ":/workspace/artifacts/automation/identity/.", "-"],
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE) as copy:
        with tarfile.open(fileobj=copy.stdout, mode="r|") as archive:
            for member in archive:
                if member.isfile() and Path(member.name).name in ("run-summary.json", "tests.trx", "identity-progress.json", "browser-errors.log", "cases.json", "plan.json", "results.json"):
                    if member.size > 8 * 1024**2:
                        raise RuntimeError("Result evidence exceeded its bound")
                    selected[member.name] = archive.extractfile(member).read()
        if copy.wait(timeout=30):
            raise RuntimeError("Result evidence copy failed")
    summaries = [(path, json.loads(data)) for path, data in selected.items() if path.endswith("run-summary.json")]
    receipt["summaries"] = [summary for _, summary in summaries]
    if not summaries:
        raise RuntimeError("Automation summary is absent")
    for index, (path, summary) in enumerate(summaries):
        (results / (str(index) + "-run-summary.json")).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    for index, (path, data) in enumerate((item for item in selected.items() if item[0].endswith("tests.trx"))):
        (results / (str(index) + "-tests.trx")).write_bytes(data)
    for index, (path, data) in enumerate((item for item in selected.items()
            if Path(item[0]).name in ("cases.json", "plan.json", "results.json"))):
        (results / (str(index) + "-" + Path(path).name)).write_bytes(data)
    for index, (path, data) in enumerate((item for item in selected.items()
            if Path(item[0]).name in ("identity-progress.json", "browser-errors.log"))):
        (results / (str(index) + "-" + Path(path).name)).write_bytes(data)
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    rows = [row for path, data in selected.items() if path.endswith("tests.trx")
            for row in ET.fromstring(data).findall(".//t:UnitTestResult", ns)]
    receipt["test_results"] = [{"name": row.attrib["testName"], "outcome": row.attrib["outcome"]} for row in rows]
    required = {"Vivarium.Acceptance.AccessJourneyTests.Installation_operator_delegates_through_Workbench_and_withdrawal_and_disable_affect_the_same_cookie",
                "Vivarium.Acceptance.OrdinaryIdentityTests.Trusted_provider_login_and_My_keys_use_real_framework_issuance_and_immediate_revocation"}
    exact = len(rows) == 2 and {row.attrib["testName"] for row in rows} == required and all(row.attrib["outcome"] == "Passed" for row in rows)
    receipt["exact_required_selection"] = exact
    receipt["outcome"] = "PASSED" if exit_code == 0 and exact and all(summary.get("cleanupComplete") for _, summary in summaries) else "FAILED"
except Exception as error:
    receipt["error"] = type(error).__name__ + ": " + str(error)
finally:
    try:
        if container_id:
            inspect_owned()
            command(["rm", "--force", container_id])
            receipt["container_removed"] = not command(["ps", "-a", "--no-trunc", "--filter", "id=" + container_id, "--format", "{{.ID}}"])
    except Exception as error:
        receipt["cleanup_error"] = type(error).__name__
        receipt["outcome"] = "FAILED"
    receipt["finished"] = time.time()
    (results / "receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    print(json.dumps({"results": str(results), "outcome": receipt["outcome"], "removed": receipt.get("container_removed")}))
sys.exit(0 if receipt["outcome"] == "PASSED" else 1)
