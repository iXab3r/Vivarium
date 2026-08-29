# TeamCity CI/CD migration handover

Status: active; local implementation complete, TeamCity bootstrap pending.

Baseline inventory contains five GitHub Actions jobs, four supported release RIDs, fourteen planned
release assets, and three authorized TeamCity agents. Cake build/release/publication targets and the
versioned Kotlin DSL are implemented. `Publish`/`PublishAll` produce runnable local trees for all four
RIDs, and the osx-arm64 tree has native `viv-server`/`viv-cli`/`viv-agent`/`viv-agent-update`
evidence. Cake limits MSBuild to one worker and the packager keeps only one component/RID intermediate
live at a time. The corrected controller package includes its settings/static assets and embeds SQLite
native bytes. Two complete sequential builds of the corrected, renamed release set produced identical
SHA-256 values for all fourteen files. The public distribution names are `viv-server`, `viv-agent`,
`viv-agent-update`, and `viv-cli`. The next phase is
server bootstrap and native evidence.

Known blockers:

- no authorized macOS TeamCity agent;
- no real Linux arm64 release-smoke evidence within the three free agent slots;
- D29 requires a controller Git bundle/prerequisite decision before stable release;
- exact controller/agent package layout is documented and implemented but still needs integration
  review with the incoming desired-configuration/system-Git work;
- GitHub App connection and trusted pull-request policy are not configured.

GitHub Actions was explicitly disabled by the project owner before TeamCity parity: remote workflow
`ci` (`343961597`) is `disabled_manually`, and its YAML was removed from the implementation branch.
There is intentionally no automatic push/pull-request CI until TeamCity is activated, so local Cake
evidence must not be presented as server parity. No public release may be created as migration
evidence.

Bootstrap order:

1. Restore TeamCity CLI authentication and rerun the read-only agent parameter audit.
2. Provision/install exact .NET SDK `10.0.301` and Cargo on Linux x64, exact SDK on the usable Windows
   agent, and add/rotate in a macOS arm64 agent. Keep at least 4 GiB free on the release assembler.
3. Create the Vivarium project from the public repository; enable `.teamcity` versioned settings using
   the default branch as settings authority. Keep pull-request integration disabled.
4. Run `CI gate` on `main`; verify TRX import, exact Linux-to-macOS artifact transfer, cancellation, and
   clean checkout behavior. Record build IDs per inventory row.
5. Add/rotate a Linux arm64 slot and run all four exact release-smoke configurations on a protected
   candidate tag. Integrate and prove the controller system-Git prerequisite on every RID.
6. Enable GitHub immutable releases and protected tags, create the publish-only TeamCity password
   parameter, exercise draft interruption/mismatch cases in staging, then unpause publication.
7. Configure the GitHub App/check publisher and make `CI gate` required. Reintroduce GitHub Actions only
   if the project owner makes a later explicit decision to use hosted runners again.
