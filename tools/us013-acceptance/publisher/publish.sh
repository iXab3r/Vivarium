#!/usr/bin/env bash
set -euo pipefail

mode="${T3_PUBLISH:-off}"
if [[ "$mode" == off ]]; then
  echo 'GitHub publication is disabled; validated assets are available in TeamCity.'
  exit 0
fi
if [[ "$mode" != draft && "$mode" != release ]]; then
  echo 'T3_PUBLISH must be off, draft, or release.' >&2
  exit 1
fi
: "${GITHUB_TOKEN:?A GitHub token with contents:write for iXab3r/vivarium-us013-e2e is required.}"

if ! command -v gh >/dev/null; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends gh
fi
repo=iXab3r/vivarium-us013-e2e
version=$(node -p 'JSON.parse(require("fs").readFileSync("ci-out/release/manifest.json")).version')
revision=$(git rev-parse HEAD)
publication_revision=551ae1f5580629142f8289d360e21122c99c8e21
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-nightly\.[0-9]{8}\.[0-9]+$ ]] || exit 1
tag="v$version"
temporary=$(mktemp -d)
retain_diagnostics() {
  status=$?
  if [[ "$status" != 0 ]]; then
    mkdir -p ci-out/publication-proof
    # Selected GitHub metadata only; never environment, headers, credentials or arbitrary notes.
    node - "$temporary/state.json" > ci-out/publication-proof/inspection.json <<'JS'
const fs = require('fs');
let r = null;
try { r = JSON.parse(fs.readFileSync(process.argv[2], 'utf8')); } catch {}
console.log(JSON.stringify(r === null ? null : {
  id:r.id, tag_name:r.tag_name, target_commitish:r.target_commitish,
  ownershipLine: r.body?.split('\n')[0]?.match(/^Vivarium US-013 acceptance source [a-f0-9]{40} target [a-f0-9]{40}$/)?.[0] ?? 'absent-or-different',
  prerelease:r.prerelease, draft:r.draft,
  assets:r.assets?.map(a=>({id:a.id,name:a.name,state:a.state,size:a.size,digest:a.digest}))
}));
JS
    if [[ -f "$temporary/inspection-error.txt" ]]; then
      cp "$temporary/inspection-error.txt" ci-out/publication-proof/inspection-error.txt
      cat "$temporary/inspection-error.txt" >&2
    fi
    echo "##teamcity[publishArtifacts 'ci-out/publication-proof/** => publication-proof']"
    cat ci-out/publication-proof/inspection.json
  fi
  rm -rf -- "$temporary"
  exit "$status"
}
trap retain_diagnostics EXIT
read_tag() {
  gh api "repos/$repo/git/matching-refs/tags/$tag" > "$temporary/refs.json"
  node -e 'const a=JSON.parse(require("fs").readFileSync(process.argv[1])).filter(x=>x.ref===process.argv[2]); if(a.length>1||a.some(x=>x.object.type!=="commit"||x.object.sha!==process.argv[3]))throw Error("Tag identity differs"); process.stdout.write(String(a.length));' \
    "$temporary/refs.json" "refs/tags/$tag" "$publication_revision"
}
if [[ $(read_tag) == 0 ]]; then
  if ! gh api --method POST "repos/$repo/git/refs" -f "ref=refs/tags/$tag" -f "sha=$publication_revision" >/dev/null; then
    echo 'Tag creation response failed; checking the actual exact target.'
  fi
  [[ $(read_tag) == 1 ]] || exit 1
fi
read_release() {
  gh api "repos/$repo/releases?per_page=100" > "$temporary/releases.json"
  node -e 'const a=JSON.parse(require("fs").readFileSync(process.argv[1]));const r=a.filter(x=>x.tag_name===process.argv[2]);if(r.length>1||a.length===100&&!r.length)throw Error("Release inventory ambiguous");process.stdout.write(JSON.stringify(r[0]??null));' \
    "$temporary/releases.json" "$tag" > "$temporary/state.json"
}
inspect_release() {
  node scripts/personal-release/publication-state.mjs "$temporary/state.json" "$revision" "$publication_revision" > "$temporary/plan.json" 2> "$temporary/inspection-error.txt"
}
read_release
if [[ $(node -p 'JSON.parse(require("fs").readFileSync(process.argv[1]))===null' "$temporary/state.json") == true ]]; then
  if ! gh release create "$tag" --repo "$repo" --target "$publication_revision" --draft --prerelease \
    --title "T3 Personal $version" --notes "Vivarium US-013 acceptance source $revision target $publication_revision
Personal T3 Code build/package only. Windows x64 installer, Linux x64 AppImage and matching CLI archives. No built application was launched."; then
    echo 'Release creation response failed; checking the actual owned draft.'
  fi
  read_release
fi
inspect_release
mapfile -t incomplete < <(node -e 'for(const id of JSON.parse(require("fs").readFileSync(process.argv[1])).incomplete)console.log(id)' "$temporary/plan.json")
for id in "${incomplete[@]}"; do
  gh api --method DELETE "repos/$repo/releases/assets/$id"
done
read_release; inspect_release
mapfile -t missing < <(node -e 'for(const name of JSON.parse(require("fs").readFileSync(process.argv[1])).missing)console.log(name)' "$temporary/plan.json")
for name in "${missing[@]}"; do
  if ! gh release upload "$tag" --repo "$repo" -- "ci-out/release/$name"; then
    echo 'Upload response failed; verifying the actual stored asset before retry.'
  fi
  read_release; inspect_release
  node -e 'const p=JSON.parse(require("fs").readFileSync(process.argv[1]));if(p.missing.includes(process.argv[2])||p.incomplete.length)throw Error("Upload not complete");' "$temporary/plan.json" "$name"
  if [[ "${VIVARIUM_US013_INTERRUPT_AFTER_FIRST_ASSET:-false}" == true ]]; then
    mkdir -p ci-out/publication-proof
    cp "$temporary/state.json" ci-out/publication-proof/partial.json
    echo "##teamcity[publishArtifacts 'ci-out/publication-proof/partial.json => publication-proof']"
    echo 'Owned acceptance fault: publisher exits after its first verified uploaded asset.'
    exit 75
  fi
done
read_release; inspect_release
node -e 'const p=JSON.parse(require("fs").readFileSync(process.argv[1]));if(p.missing.length||p.incomplete.length)throw Error("Release assets incomplete");' "$temporary/plan.json"
if [[ "$mode" == release && $(node -p 'JSON.parse(require("fs").readFileSync(process.argv[1])).published' "$temporary/plan.json") == false ]]; then
  if ! gh release edit "$tag" --repo "$repo" --draft=false; then
    echo 'Publish response failed; verifying actual publication.'
  fi
  read_release; inspect_release
  node -e 'if(!JSON.parse(require("fs").readFileSync(process.argv[1])).published)throw Error("Release not published");' "$temporary/plan.json"
fi
[[ $(read_tag) == 1 ]] || exit 1
echo "Release: https://github.com/$repo/releases/tag/$tag"
