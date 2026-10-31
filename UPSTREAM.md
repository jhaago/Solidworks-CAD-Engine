# Upstream baseline

Source: https://github.com/soheil-mp/solidworks-mcp
Pinned commit: `f450dabcd02a9a7ba705ee9e18a77aaeee9fe802` (27 October 2026).
Licence: MIT; original LICENSE and THIRD-PARTY-NOTICES.md retained without modification.

The target was verified empty on 31 October 2026. Anonymous Git clone retained all 70 upstream commits locally, but Git push has no credentials. The authenticated connector cannot seed an empty repository from a foreign commit. Therefore this repository imports the complete 182-file pinned source snapshot; no proprietary SOLIDWORKS binaries are included.

For future upstream updates:

```powershell
git remote add upstream https://github.com/soheil-mp/solidworks-mcp.git
git fetch upstream
# Initial snapshot import only: connect the recorded upstream ancestry.
git switch -c maintenance/upstream-ancestry
git merge --strategy=ours --allow-unrelated-histories f450dabcd02a9a7ba705ee9e18a77aaeee9fe802
# Review this ancestry-only commit in a PR before normal upstream merges.
# Thereafter: git switch -c maintenance/upstream-update; git merge upstream/main
```

Review upstream source/licence/build changes and tests in an update PR. Resolve conflicts in integration files deliberately. Never replace this repository or force-push to add fork metadata. The baseline tree is pinned; custom development remains on integration branches.

See `docs/INTEGRATION.md` on the adapter PR for build, start, acceptance, evidence and rollout details.
