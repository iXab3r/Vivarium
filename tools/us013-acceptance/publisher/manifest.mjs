import * as NodeCrypto from "node:crypto";
import * as NodeFS from "node:fs";
import * as NodeFSP from "node:fs/promises";
import * as NodePath from "node:path";

export async function sha256(file) {
  const hash = NodeCrypto.createHash("sha256");
  for await (const chunk of NodeFS.createReadStream(file)) hash.update(chunk);
  return hash.digest("hex");
}

export async function writeManifest(directory, identity) {
  const files = {};
  for (const entry of await NodeFSP.readdir(directory, { withFileTypes: true })) {
    if (!entry.isFile() || entry.name === "manifest.json") continue;
    files[entry.name] = await sha256(NodePath.join(directory, entry.name));
  }
  await NodeFSP.writeFile(
    NodePath.join(directory, "manifest.json"),
    `${JSON.stringify({ ...identity, files }, null, 2)}\n`,
  );
}

export async function verifyManifest(directory, revision, expectedVersion) {
  const manifest = JSON.parse(
    await NodeFSP.readFile(NodePath.join(directory, "manifest.json"), "utf8"),
  );
  if (manifest.revision !== revision || manifest.repository !== "iXab3r/vivarium-us013-e2e") {
    throw new Error("Release artifacts belong to a different source revision or repository.");
  }
  if (!/^\d+\.\d+\.\d+-nightly\.\d{8}\.\d+$/.test(manifest.version)) {
    throw new Error("Expected a personal nightly release version.");
  }
  if (expectedVersion !== undefined && manifest.version !== expectedVersion)
    throw new Error("Release artifacts belong to a different release version.");
  if (!manifest.files || Object.keys(manifest.files).length === 0)
    throw new Error("Empty release manifest.");
  for (const [name, digest] of Object.entries(manifest.files)) {
    if (NodePath.basename(name) !== name || name.includes("\\") || !/^[a-f0-9]{64}$/.test(digest)) {
      throw new Error(`Invalid manifest entry: ${name}`);
    }
    if ((await sha256(NodePath.join(directory, name))) !== digest)
      throw new Error(`Artifact checksum mismatch: ${name}`);
  }
  return manifest;
}
