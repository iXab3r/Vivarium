"""Bounded offline fake-gh transport; executes the real publisher in installed Node Docker."""
import hashlib, json, pathlib, subprocess, tempfile
root = pathlib.Path(__file__).resolve().parents[2]
image = "node:24-bookworm-slim"
for case in ["response-loss", "wrong-identity", "wrong-readback-id", "exact-created-id"]:
 with tempfile.TemporaryDirectory(prefix="us013-transport-") as temporary:
  work = pathlib.Path(temporary)
  (work/"ci-out/release").mkdir(parents=True)
  payload=b"fixture bytes"; digest=hashlib.sha256(payload).hexdigest()
  (work/"ci-out/release/payload.zip").write_bytes(payload)
  revision="a"*40; target="551ae1f5580629142f8289d360e21122c99c8e21"; version="0.1.6-nightly.20261104.1"
  (work/"ci-out/release/manifest.json").write_text(json.dumps(dict(repository="iXab3r/vivarium-us013-e2e",revision=revision,version=version,files={"payload.zip":digest})))
  (work/"ci-out/release/SHA256SUMS").write_text(digest+"  payload.zip\n", newline="\n")
  release=dict(id=123,tag_name="v"+version,target_commitish=target,body=f"Vivarium US-013 acceptance source {revision} target {target}",draft=True,prerelease=True,assets=[])
  if case=="wrong-identity": release["tag_name"]="wrong"
  (work/"created.json").write_text(json.dumps(release))
  if case=="wrong-readback-id": release["id"]=124
  (work/"readback.json").write_text(json.dumps(release))
  (work/"fake.sh").write_text("""#!/bin/bash
set -e
mkdir -p scripts/personal-release
cp /adapter/*.mjs scripts/personal-release/
git() { echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; }
gh() {
 echo "$*" >> calls
 case "$*" in
  *matching-refs*) echo '[{"ref":"refs/tags/v0.1.6-nightly.20261104.1","object":{"type":"commit","sha":"551ae1f5580629142f8289d360e21122c99c8e21"}}]' ;;
  *'releases?per_page=100'*) echo '[]' ;;
  *'uploads.github.com'*) echo upload-reached; return 42 ;;
  *'--method POST'*) if [[ "$CASE" == response-loss ]]; then return 22; fi; cat created.json ;;
  *'releases/123'*) cat readback.json ;;
  *'release upload'*) echo upload-reached; return 42 ;;
  *) return 99 ;;
 esac
}
export -f gh git
sed 's/\\r$//' /adapter/publish.sh > publisher.sh
bash publisher.sh
""", newline="\n")
  result=subprocess.run(["docker","run","--rm","--pull=never","--network","none","-v",str(work)+":/work","-v",str(root/"tools/us013-acceptance/publisher")+":/adapter:ro","-w","/work","-e","CASE="+case,"-e","T3_PUBLISH=release","-e","GITHUB_TOKEN=offline-fixture",image,"bash","fake.sh"],capture_output=True,text=True,timeout=60)
  calls=(work/"calls").read_text() if (work/"calls").exists() else (_ for _ in ()).throw(RuntimeError(result.stderr))
  assert result.returncode != 0, case
  assert ("uploads.github.com" in calls)==(case=="exact-created-id"), (case,result.stdout,result.stderr)
  if case=="exact-created-id": assert "releases/123" in calls
  print(case+": PASS")
