import test from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs/promises";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import os from "node:os";
import { createHash } from "node:crypto";
import { inspectPublication } from "./publisher/publication-state.mjs";
import { writeManifest, sha256 } from "./publisher/manifest.mjs";
const provenance = JSON.parse(await fs.readFile(new URL("./publisher/manifest-provenance.json", import.meta.url)));
assert.equal(createHash("sha256").update(await fs.readFile(new URL("./publisher/manifest.mjs", import.meta.url))).digest("hex"), provenance.sha256);

const revision = "a".repeat(40), target = "b".repeat(40), version = "0.0.1-nightly.20261104.1";
const root = await fs.mkdtemp(path.join(os.tmpdir(), "us013-publication-"));
test.after(() => fs.rm(root, { recursive: true, force: true }));
async function fixture() {
  const directory = path.join(root, randomUUID()); await fs.mkdir(directory, { recursive: true });
  await fs.writeFile(path.join(directory, "owned.zip"), "owned-publication-bytes\n");
  await writeManifest(directory, { repository: "iXab3r/vivarium-us013-e2e", version, revision });
  const manifest = JSON.parse(await fs.readFile(path.join(directory, "manifest.json")));
  await fs.writeFile(path.join(directory, "SHA256SUMS"), Object.keys(manifest.files).sort().map(name => `${manifest.files[name]}  ${name}\n`).join(""));
  const assets = [];
  for (const [index, name] of ["SHA256SUMS", "owned.zip", "manifest.json"].entries()) assets.push({ id: index + 10, name,
    state: "uploaded", size: (await fs.stat(path.join(directory, name))).size,
    digest: "sha256:" + await sha256(path.join(directory, name)) });
  return { directory, release: { tag_name: `v${version}`, target_commitish: target, draft: true, prerelease: true,
    body: `Vivarium US-013 acceptance source ${revision} target ${target}\nOwned fixture`, assets } };
}
test("Exact published retry is accepted without missing assets", async () => {
  const f = await fixture(); f.release.draft = false;
  assert.deepEqual(await inspectPublication(f.directory, f.release, revision, target), { missing: [], incomplete: [], published: true });
});
test("An owned partial draft returns only its missing file", async () => {
  const f = await fixture(); f.release.assets.pop();
  assert.deepEqual((await inspectPublication(f.directory, f.release, revision, target)).missing, ["manifest.json"]);
});
test("Published partial assets cannot manufacture success", async () => {
  const f = await fixture(); f.release.draft = false; f.release.assets.pop();
  await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
});
test("Wrong metadata, bytes, extra files and local corruption refuse overwrite", async t => {
  for (const kind of ["source", "target", "tag", "prerelease", "draft-type", "first-line", "digest", "size", "extra", "duplicate", "local"]) await t.test(kind, async () => {
    const f = await fixture();
    if (kind === "source") f.release.body = "unrelated release";
    if (kind === "prerelease") f.release.prerelease = false;
    if (kind === "draft-type") f.release.draft = "true";
    if (kind === "first-line") f.release.body = f.release.body.replace("\n", "\r\n");
    if (kind === "target") f.release.target_commitish = "c".repeat(40);
    if (kind === "tag") f.release.tag_name += ".other";
    if (kind === "digest") f.release.assets[0].digest = "sha256:" + "0".repeat(64);
    if (kind === "size") f.release.assets[0].size++;
    if (kind === "extra") f.release.assets.push({ ...f.release.assets[0], name: "unexpected.zip" });
    if (kind === "duplicate") f.release.assets.push(f.release.assets[0]);
    if (kind === "local") await fs.writeFile(path.join(f.directory, "owned.zip"), "different-local-bytes");
    await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
  });
});
test("Only an empty starter in the exact owned draft is removable", async () => {
  const f = await fixture(); f.release.assets[0] = { ...f.release.assets[0], state: "starter", size: 0, digest: null };
  assert.deepEqual((await inspectPublication(f.directory, f.release, revision, target)).incomplete, [10]);
  f.release.assets[0].size = 1;
  await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
  f.release.assets[0].size = 0; f.release.draft = false;
  await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
});

// Read-only observation after the failed boundary; this does not reconstruct its deleted response.
test("Observed release 434059301 passes exact ownership but has no publication bytes", async () => {
  const observed = JSON.parse(await fs.readFile(new URL("./observed-release-434059301.json", import.meta.url), "utf8"));
  const source = "32610536e7097cfee83016355d4b97e45baf8d2d";
  const f = await fixture();
  await fs.rm(path.join(f.directory, "SHA256SUMS"));
  await writeManifest(f.directory, { repository: "iXab3r/vivarium-us013-e2e", version: "0.1.6-nightly.20261104.880", revision: source });
  await fs.writeFile(path.join(f.directory, "SHA256SUMS"), `${await sha256(path.join(f.directory, "owned.zip"))}  owned.zip\n`);
  const plan = await inspectPublication(f.directory, observed, source, observed.target_commitish);
  plan.missing.sort();
  assert.deepEqual(plan, { missing: ["SHA256SUMS", "manifest.json", "owned.zip"], incomplete: [], published: false });
  await assert.rejects(inspectPublication(f.directory, null, source, observed.target_commitish));
});

 test("Null selected response reports every exact identity predicate", async () => {
  const f = await fixture();
  await assert.rejects(inspectPublication(f.directory, null, revision, target), /selected, tag, target, ownership, prerelease, draftType/);
 });

async function stagedFixture() {
  const f = await fixture();
  await fs.rm(path.join(f.directory, "SHA256SUMS"));
  for (const name of ["desktop.exe", "desktop.blockmap", "latest.yml", "cli-linux-x64.tar.gz", "desktop.AppImage", "latest-linux.yml"])
    await fs.writeFile(path.join(f.directory, name), `payload ${name}\n`);
  await writeManifest(f.directory, { repository: "iXab3r/vivarium-us013-e2e", version, revision });
  const manifest = JSON.parse(await fs.readFile(path.join(f.directory, "manifest.json")));
  f.release.assets = f.release.assets.filter(a => a.name !== "manifest.json" && a.name !== "SHA256SUMS");
  f.checksums = Object.keys(manifest.files).sort().map(name => `${manifest.files[name]}  ${name}\n`).join("");
  await fs.writeFile(path.join(f.directory, "SHA256SUMS"), f.checksums);
  return f;
}
test("Actual stage-release shape verifies derived bytes in missing and remote inventories", async () => {
  const f = await stagedFixture();
  assert.ok((await inspectPublication(f.directory, f.release, revision, target)).missing.includes("SHA256SUMS"));
  const names = await fs.readdir(f.directory);
  f.release.assets = await Promise.all(names.map(async (name, id) => ({ id, name, state: "uploaded",
    size: (await fs.stat(path.join(f.directory, name))).size, digest: "sha256:" + await sha256(path.join(f.directory, name)) })));
  f.release.draft = false;
  assert.deepEqual(await inspectPublication(f.directory, f.release, revision, target), { missing: [], incomplete: [], published: true });
  f.release.assets.find(a => a.name === "SHA256SUMS").digest = "sha256:" + "0".repeat(64);
  await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
});
test("Derived metadata rejects incomplete, ambiguous and tampered inventories", async t => {
  for (const kind of ["duplicate", "missing", "extra", "path", "digest", "separator", "crlf", "order", "no-newline", "binary", "unknown-file", "payload-tamper", "self"])
    await t.test(kind, async () => {
      const f = await stagedFixture();
      const lines = f.checksums.trimEnd().split("\n");
      let bytes = f.checksums;
      if (kind === "duplicate") bytes += lines[0] + "\n";
      if (kind === "missing") bytes = lines.slice(1).join("\n") + "\n";
      if (kind === "extra") bytes += "0".repeat(64) + "  extra.zip\n";
      if (kind === "path") bytes = bytes.replace("  cli-linux", "  ../cli-linux");
      if (kind === "digest") bytes = "0".repeat(64) + bytes.slice(64);
      if (kind === "separator") bytes = bytes.replace("  ", " *");
      if (kind === "crlf") bytes = bytes.replaceAll("\n", "\r\n");
      if (kind === "order") bytes = lines.reverse().join("\n") + "\n";
      if (kind === "no-newline") bytes = bytes.trimEnd();
      if (kind === "binary") bytes += "\0";
      if (kind === "unknown-file") await fs.writeFile(path.join(f.directory, "other-sums"), bytes);
      if (kind === "payload-tamper") await fs.appendFile(path.join(f.directory, "desktop.exe"), "changed");
      await fs.writeFile(path.join(f.directory, "SHA256SUMS"), bytes);
      if (kind === "self") await writeManifest(f.directory, { repository: "iXab3r/vivarium-us013-e2e", version, revision });
      await assert.rejects(inspectPublication(f.directory, f.release, revision, target));
    });
});

test("Deleting the complete derived checksum file fails closed", async () => {
  const f = await stagedFixture();
  await fs.rm(path.join(f.directory, "SHA256SUMS"));
  await assert.rejects(inspectPublication(f.directory, f.release, revision, target), /SHA256SUMS/);
});
