# Private PostgreSQL/Chiral Host

This recipe targets one dedicated Linux x64 VM and a standalone Portainer Docker endpoint.
It packages the D34/D35 Host; [docker-compose.yml](docker-compose.yml) explicitly retains the
legacy SQLite controller. TeamCity's Compile → Release → GitHub/Docker publishers produce the
release. Select the same verified version/source SHA from both publishers; deploy the immutable
Docker tag or digest through Portainer. Neither publication nor this template performs deployment.

Owner-approved first-launch scope (2026-11-05): use only the installation superuser.
Keep `SuperuserEnabled=true` and `Providers=[]` in the private initialization input.
External OIDC and ordinary-user login are separate follow-up work, not this deployment's
acceptance prerequisite. Protect the generated startup password in private operator logs;
do not publish it in Git, build output or fleet logs. This scope does not permit superuser
bearer/API-key authentication or change the existing cookie and authorization contracts.

## Prepare the instance

The native VM configuration owns PostgreSQL, firewall, DNS, reverse proxy and Docker prerequisites.
Provision PostgreSQL on loopback with a dedicated database, maintenance principal and runtime
principal. Runtime requires the ordinary application tables/sequence permissions; migrations use
the separate maintenance connection. Keep the Host stopped during schema maintenance.
The optional [private PostgreSQL stack](private-postgres.compose.yml) uses the same pinned17.10
image as the selected real-PG proofs, loopback binding, separate SSD data and a protected password
file. Deploy it through Portainer before initialization; create separate maintenance/runtime roles
through private SQL input. No password belongs in a stack environment.

The VM's Docker Engine is a trusted administrative dependency of the Host coordinator. It must
carry only this dedicated Vivarium control plane and its definition evaluators. Do not enroll a
build Agent on this VM or connect production workload Engines. Grant the container's `app` UID
(1654 in the .NET base image) access to the selected Engine socket through its numeric group.
This grants Engine administrator authority to the trusted Host, including effective VM root;
`cap_drop` does not remove that authority. Project C# receives no socket or Host mounts: the
existing D43 evaluator creates networkless non-root, read-only, resource-bounded containers and
transfers bounded source through stdin. This is a container boundary, not VM isolation against
kernel/daemon compromise. A second rootful daemon does not eliminate the trusted Host's VM-root
authority. Do not expose the Engine over unauthenticated TCP.

Preload the exact evaluator SDK on that Engine before catalogue reconciliation:

```sh
docker pull mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
```

Create the private state directory before Portainer deployment, owned by UID 1654 and mode 0700.
It must persist settings, five private certificates, Data Protection keys, object bytes and the
managed control-Git checkout. PostgreSQL has its own durable data. Back up these together with
the instance configuration; lost Data Protection keys make encrypted queued inputs/assignments
unrecoverable. Protect backups and private startup logs as credentials.
The templates select Docker's bounded `local` log driver for Host/PostgreSQL, keeping private
startup credentials and SQL diagnostics outside the fleet collector's JSON-file inputs. Authorized
Portainer/Engine administrators can still read these logs; do not forward them without redaction.

Copy [private-host.init.sample.json](private-host.init.sample.json) to a private file outside Git.
Replace its runtime password and Storage signing-key placeholders; use 32 cryptographically random
bytes encoded as base64 for the latter. Provision persistent RSA PKCS#12 signing/encryption
certificates and the HTTPS certificate at the specified paths. Protect private keys with filesystem
permissions; add schema-supported `Password`/`CertificatePassword` fields when using encrypted PFX.
The sample's `DefinitionSources` requires a clean `main` checkout containing valid schema-2 C#
declarations; prepare it using [C# activation](../docs/design/csharp-activation.md). Omit
`DefinitionSources` until that source is prepared; do not fabricate an active catalogue.

Initialize through the release executable's existing stdin commands. For example, from the VM's
private administrative shell with the same image and state mount as the stack:

```sh
docker run --rm -i --network host --mount type=bind,src="$VIVARIUM_STATE",dst=/var/lib/vivarium \
  "$VIVARIUM_IMAGE" init --settings-directory /var/lib/vivarium/settings < /private/init.json
docker run --rm -i --network host --mount type=bind,src="$VIVARIUM_STATE",dst=/var/lib/vivarium \
  "$VIVARIUM_IMAGE" migrate --settings-directory /var/lib/vivarium/settings < /private/migrate.json
```

`migrate.json` contains only `{"ConnectionString":"<private maintenance connection>"}`.
The maintenance connection is never saved as runtime configuration. Delete the temporary private
input files after successful initialization. Subsequent settings changes use Chiral's saved/active
restart lifecycle; Compose environment variables do not override the Host/Database/Identity/Storage
configuration. `nodes --settings-directory ...` returns masked settings for diagnosis.

## Private ingress and stable Agent trust

The selected layout separates browser TLS from the stable Agent leaf without changing trust rules:

- `PublicOrigin=https://viv.eyeauras.net` is the canonical browser/OIDC origin.
- `ListenUrl=https://100.120.230.43:8443` binds the Host to the tailnet address. Agents enroll against
  this URL with its independently verified stable certificate fingerprint. AgentHub and all relative
  `/blobs/{sha256}` transfers use that same origin and pin.
- The native reverse proxy binds only the VM's tailnet address on 443, presents the normal publicly
  trusted browser certificate, and proxies HTTPS to the Host on 8443. Configure upstream TLS
  verification using the private Host certificate/CA and a matching SAN/SNI name; preserve the
  original `Host: viv.eyeauras.net` header, including any explicit public port. Proxy all browser/API
  paths and allow streaming/SSE without response buffering.
- The Host has no forwarded-header middleware. HTTPS upstream supplies the correct request scheme;
  preserved Host supplies browser redirect/callback authority. Do not depend on `X-Forwarded-*`.
  The token UI's self-client calls `PublicOrigin`; the VM/container must resolve and reach that
  origin on 443 and validate its public certificate. External OIDC callbacks also use PublicOrigin.
- Storage remains `http://127.0.0.1:8444`; its signed URLs are internal and never reach Agents or
  browsers. PostgreSQL remains loopback-only. Do not route either through the public proxy.

Provision browser certificates through the native ingress's existing ACME/DNS lifecycle. Restrict
443/8443 to the intended tailnet and deny LAN/public access in the guest firewall. Host networking
avoids Docker-published-port forwarding rules; it does not itself provide that firewall restriction.
Keep the Agent leaf stable across browser ACME renewal. Replacing that leaf requires an authenticated
re-pin procedure; never disable Agent verification or silently enroll against the rotating proxy leaf.

## Deploy and verify

Use [private-host.compose.yml](private-host.compose.yml) as the Portainer stack input. Supply
`VIVARIUM_IMAGE`, `VIVARIUM_STATE`, `VIVARIUM_ENGINE_SOCKET` and `VIVARIUM_ENGINE_GID` as deployment
inputs, plus `VIVARIUM_OBJECTS` for the separately mounted artifact disk. The bind paths must already
exist and the Host UID must own the state and object directories. No database passwords, PFX passwords or Storage keys
belong in the stack environment. The image's default command is the new Host `run` command;
`--version`, `init`, `nodes` and `migrate` remain explicit executable commands.

For a 2-vCPU/4-GiB VM the template bounds the Host to one CPU, 1536 MiB and 256 processes. The
evaluator separately retains its existing one-CPU/512-MiB/128-process bound and serialized gate.
Budget remaining RAM for PostgreSQL, Docker, ingress and the OS; these are initial deployment
budgets, not measured capacity claims. Host networking and bind mounts make this a standalone
Linux Compose template, not a Swarm service definition.

Before accepting the instance, verify trusted browser TLS and login at PublicOrigin, token creation
and redemption through its real callback, `/health/live` and `/health/ready`, masked node state,
catalogue reconciliation through the actual evaluator, and an independently enrolled Agent's
run/output/artifact bytes over the stable endpoint. Verify unauthenticated requests remain denied,
loopback Storage/PostgreSQL remain unreachable remotely, and VM restart retains identity/settings/
catalogue/bytes. The image packaging probes only check executable version, Docker CLI and Chiral
command availability; they do not prove this operational acceptance.

To upgrade, stop the stack, back up the complete persistence boundary, apply explicit migrations
from the new verified release, then recreate the stack with that release. A downgrade requires a
compatible database or restoring the complete prior backup; replacing an image does not reverse
schema changes. Retain the previous immutable image and backup until the new instance passes checks.
