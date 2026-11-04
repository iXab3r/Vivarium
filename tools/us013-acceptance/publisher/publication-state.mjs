// Owned US-013 recipe adapter. Pure file/metadata verification; never launches built T3.
import * as fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sha256, verifyManifest } from "./manifest.mjs";

export async function inspectPublication(directory, release, revision, target) {
  const manifest = await verifyManifest(directory, revision);
  const marker = `Vivarium US-013 acceptance source ${revision} target ${target}`;
  const predicates = {
    selected: release !== null && typeof release === "object",
    tag: release?.tag_name === `v${manifest.version}`,
    target: release?.target_commitish === target,
    ownership: release?.body?.split("\n")[0] === marker,
    prerelease: release?.prerelease === true,
    draftType: typeof release?.draft === "boolean",
  };
  const failed = Object.entries(predicates).filter(([, pass]) => !pass).map(([name]) => name);
  if (failed.length) throw new Error("Release identity/ownership differs: " + failed.join(", "));
  const expected = new Map();
  const entries = await fs.readdir(directory, { withFileTypes: true });
  if (entries.length > 16 || entries.some(e => !e.isFile() || !/^[A-Za-z0-9][A-Za-z0-9._+-]{0,180}$/.test(e.name)))
    throw new Error("Release directory exceeds its selected flat-file profile.");
  // stage-release writes this derived metadata after the payload manifest.
  // Compare bytes, not parsed lines: this also rejects duplicate/missing/extra
  // entries, alternate separators, traversal, invalid digests and encodings.
  if (Object.hasOwn(manifest.files, "SHA256SUMS") || Object.hasOwn(manifest.files, "manifest.json"))
    throw new Error("Derived metadata cannot be a manifest payload.");
  const payloadNames = Object.keys(manifest.files).sort();
  if (payloadNames.some(name => !/^[A-Za-z0-9][A-Za-z0-9._+-]{0,180}$/.test(name)))
    throw new Error("Invalid release payload name.");
  if (entries.some(entry => entry.name === "SHA256SUMS")) {
    const checksumPath = path.join(directory, "SHA256SUMS");
    const canonical = Buffer.from(payloadNames.map(name => `${manifest.files[name]}  ${name}\n`).join(""));
    if ((await fs.stat(checksumPath)).size !== canonical.length ||
        !(await fs.readFile(checksumPath)).equals(canonical))
      throw new Error("Derived SHA256SUMS bytes differ from the complete payload manifest.");
  }
  for (const entry of entries) {
    if (entry.name !== "manifest.json" && entry.name !== "SHA256SUMS" && !Object.hasOwn(manifest.files, entry.name))
      throw new Error("Release directory contains an unmanifested file.");
    const stat = await fs.stat(path.join(directory, entry.name));
    if (stat.size > 2 * 1024 ** 3) throw new Error("Release asset exceeds its byte bound.");
    expected.set(entry.name, { size: stat.size, digest: "sha256:" + await sha256(path.join(directory, entry.name)) });
  }
  if (!Array.isArray(release.assets) || release.assets.length > 16) throw new Error("Remote asset inventory is invalid.");
  const names = new Set(), incomplete = [];
  for (const asset of release.assets) {
    const file = expected.get(asset.name);
    if (!file || names.has(asset.name)) throw new Error("Remote release has unexpected or duplicate assets.");
    names.add(asset.name);
    // GitHub documents zero-byte starter remnants after failed uploads. Only our exact draft may remove one.
    if (release.draft && asset.state === "starter" && asset.size === 0 && !asset.digest && Number.isSafeInteger(asset.id)) {
      incomplete.push(asset.id); continue;
    }
    if (asset.state !== "uploaded" || asset.size !== file.size || asset.digest !== file.digest)
      throw new Error("Remote asset bytes differ; refusing to overwrite them.");
  }
  const missing = [...expected.keys()].filter(name => !names.has(name));
  if (!release.draft && (missing.length || incomplete.length)) throw new Error("Published release is incomplete.");
  return { missing, incomplete, published: !release.draft };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [state, revision, target] = process.argv.slice(2);
  console.log(JSON.stringify(await inspectPublication("ci-out/release", JSON.parse(await fs.readFile(state, "utf8")), revision, target)));
}
