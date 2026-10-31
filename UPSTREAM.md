# Upstream baseline

Source: https://github.com/soheil-mp/solidworks-mcp
Pinned commit: `05f368266e64ba0a570f8a9147b6aa7b701bb380` (31 October 2026).
Licence: MIT. Original LICENSE and THIRD-PARTY-NOTICES.md are unchanged.

## Import audit

At session start this repository was **not empty**: main was
`c53ce56756c1d80387ae7c1e8fe70219f3435cb2`, containing only UPSTREAM.md.
Its claim that 182 source files had already been imported was incorrect.
This commit imports all 185 tracked files from the pin above, including all
service families, MCP hosting, tests, tools, build configuration and CI.
No proprietary SOLIDWORKS assemblies, CAD documents or secrets are included.

Anonymous clone/fetch retained upstream history locally, but direct Git push
has no credentials in this environment. The authenticated GitHub connector
imports a complete pinned snapshot while preserving this repository's main
history. The upstream commit and tree can be checked with git fetch.

## Updates

```powershell
git remote add upstream https://github.com/soheil-mp/solidworks-mcp.git
git fetch upstream
git switch -c maintenance/upstream-ancestry
# Review the snapshot against this exact source before linking ancestry:
git diff 05f368266e64ba0a570f8a9147b6aa7b701bb380 -- . ':!UPSTREAM.md'
# Connect the imported baseline, never upstream/main at an unknown newer SHA:
git merge -s ours --allow-unrelated-histories 05f368266e64ba0a570f8a9147b6aa7b701bb380
# Submit ancestry-only PR; do not merge custom development without approval.
# After it is reviewed/merged, use ordinary upstream merge PRs.
git switch -c maintenance/upstream-update
git merge upstream/main
```

Review licence, dependency, API and script changes on every update. Keep
integration changes separate. Do not recreate this repository or force-push.
Project B (`czuryk/SolidworksMCP`, MIT, inspected at
`eccb8af4aa76416baf2a5ca99b256da19b73d9b6`) is a secondary source only;
no second engine or heavyweight reconstruction dependencies are imported.
