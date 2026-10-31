# Upstream engine integration prototype

## State and architecture

The complete Project A snapshot at `05f368266e64ba0a570f8a9147b6aa7b701bb380`
is on main (baseline import `92b259ae6f68b7576e80dcc48edc249a86b1f81c`).
See [UPSTREAM.md](../UPSTREAM.md) for the audit, licence and update procedure.
The target initially contained a provenance-only commit, not an actual source import.

All upstream Core service families, tool classes, MCP hosting, STA dispatcher,
ownership guards, verification, tests and build configuration are retained.
The engine stays .NET 10/Windows. The application stays .NET Framework 4.8.
`adapters/SolidWorksCadEngine.Adapter` targets .NET Standard 2.0 and references
only Newtonsoft.Json 13.0.3. It launches upstream's existing stdio server;
there is no second engine, COM scheduler, proxy server or new remote route.
Project B was inspected (MIT, pin in UPSTREAM.md); none of its Python, CUDA or
image-processing runtime is imported in this phase.

One deliberate native extension is `dimension_rename`, implemented as a Core
service plus normal MCP tool, for meaningful parameter names in the acceptance
part. It reuses upstream ownership/STA execution, verifies name lookup and
unchanged value, and attempts name restoration on failure. Pure name policy
has offline tests; the COM implementation and added live test are **not yet
compiled or native-verified** here. API reference:
https://help.solidworks.com/2025/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IDimension~Name.html
The supplied dimension must exist and the new name must be unqualified.

## Reproducible headless build

Prerequisites: .NET 10 SDK (global.json permits latest installed .NET 10 feature
band), normal NuGet access. Adapter package versions are pinned in its project.
No installation script is needed for these checks.

```powershell
dotnet build adapters/SolidWorksCadEngine.Adapter -c Release
dotnet run --project tests/SolidWorksCadEngine.AdapterTests -c Release
dotnet build tools/CadEngine.Acceptance -c Release
dotnet test --project tests/SolidWorksMcp.UnitTests
python tools/check_repo.py
```

`dotnet run --project tests/SolidWorksMcp.UnitTests` runs the upstream xUnit
executable directly where the CLI test launcher's local IPC is restricted.
Windows is the intended platform of that upstream suite: its STA and Windows
path tests fail on Linux. Do not suppress those tests or treat them as passes.
Adapter subprocess tests run on Linux and Windows in `.github/workflows/adapter.yml`;
upstream Windows offline CI is retained in `ci.yml`. CI does not claim to build
Core/Server or exercise native CAD on GitHub-hosted machines.

## Windows engine build and real MCP probe

Install a licensed SOLIDWORKS, its API interop assemblies (including Simulation
interop as required upstream), and .NET 10 SDK. Set `SOLIDWORKS_DIR` to the
installation folder containing `api\redist`; no version-2020 requirement applies.

```powershell
$env:SOLIDWORKS_DIR = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
dotnet build SolidWorksMcp.slnx -c Release
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet publish src/SolidWorksMcp.Server -c Release -o artifacts/engine
if ($LASTEXITCODE) { throw 'Publish failed' }
& artifacts/engine/solidworks-mcp.exe --check-catalog
if ($LASTEXITCODE) { throw 'Catalog failed' }
dotnet run --project tools/CadEngine.Acceptance -c Release -- --engine "$PWD/artifacts/engine/solidworks-mcp.exe" --output "$env:TEMP/cad-engine-probe-$([guid]::NewGuid().ToString('N'))"
if ($LASTEXITCODE) { throw 'Real MCP probe failed' }
python tools/mcp_smoke.py artifacts/engine/solidworks-mcp.exe
if ($LASTEXITCODE) { throw 'Upstream protocol smoke failed' }
```

The probe completes real initialization, saves the entire discovered catalog
and schemas, and invokes `sw_status` through the adapter. It does not construct
a part. `--engine` also accepts the published DLL (through the dotnet host).
No real-engine probe succeeded in the Linux development environment: the
engine cannot build here without proprietary interops and cannot execute
Windows COM/STA natively. Fixture protocol success is explicitly separate.

`tools/install.ps1` was reviewed: it publishes locally and, unless `-NoRegister`
is given, changes Claude's MCP registration. Use the direct build above for
this application; the installer has not been executed in this session.

## Native synthetic plate acceptance

Save existing work and start SOLIDWORKS at the same privilege level as this
command. Do not run a second client or work manually in CAD during acceptance.
The runner creates its own uniquely named owned part, supplies its returned
handle to every modelling/inspection tool, never opens an original document,
and never overwrites an existing evidence folder or model.

```powershell
$evidence = Join-Path $env:TEMP ('cad-engine-plate-' + [guid]::NewGuid().ToString('N'))
dotnet run --project tools/CadEngine.Acceptance -c Release -- --engine "$PWD/artifacts/engine/solidworks-mcp.exe" --output $evidence --native
if ($LASTEXITCODE) { throw "Native acceptance failed; retain $evidence and check recovery state" }
$env:SWMCP_LIVE_TESTS = '1'
dotnet test --project tests/SolidWorksMcp.LiveTests --filter-class '*DimensionNamingLiveTests'
```

Sequence: instruction dimensions → doc_new → named profile and driving
width/height → PlateBody extrude → named hole sketch with diameter/X/Y
constraints → ThroughHole cut → rebuild and inspection → native save →
`dimension_set PlateWidth@MountingProfile = 150` → normal rebuild → inspection
and second save. The same document handle and feature inventory are retained.

| Native parameter | Initial | After edit |
|---|---:|---:|
| PlateWidth@MountingProfile | 120 mm | 150 mm |
| PlateHeight@MountingProfile | 60 mm | 60 mm |
| PlateThickness@PlateBody | 8 mm | 8 mm |
| HoleDiameter@MountingHole | 8 mm | 8 mm |
| HoleX@MountingHole | 20 mm | 20 mm |
| HoleY@MountingHole | 10 mm | 10 mm |

The hole location is **sketch** X/Y on the Top plane; SOLIDWORKS world axes
are different. Oracles are one valid solid, bounding sizes [width,8,60] mm,
volume `(width*60 - pi*4^2)*8` mm³, six driving dimensions, unchanged hole
coordinates and feature history, and valid OLE compound-file headers for both
saved native parts. Full MCP results, catalog, initialization, journal,
inspection responses and screenshot are retained locally. `acceptance.json`
is written only after all native checks pass. No automatic cleanup tool call
follows a failure or uncertain outcome.

## Execution, failure and recovery contract

- `EngineOptions.Enabled` defaults false. Executable/arguments are trusted
  local configuration, not model output. No shell is invoked.
- Every job owns `McpBackendSession` and `FileCadExecutionBoundary` until the
  entire sequence ends. The lease is the application's
  `%LOCALAPPDATA%\SolidWorksCadAgent\cad-native-control.lock` with FileShare.None.
  Install the corresponding application change as well: older app binaries
  do not know about the recovery marker.
- Discovery retains schemas and annotations. Invocation works across all
  discovered families without a handwritten per-feature adapter. All content
  blocks, structuredContent, metadata and errors remain in the returned JSON.
- The adapter excludes the automation toolset, forces `SWMCP_VBA=off`, and
  refuses `vba_run` even if a server advertises it. Tool arguments are validated
  by the real upstream server, not reimplemented by this client.
- Before each call, a durable `.recovery-required` marker and journal dispatch
  are flushed. A definite response is journaled before the marker is removed.
  Journal files contain local CAD data and should stay within the job workspace.
- Cancellation while queued sends no operation. Cancellation after dispatch,
  client timeout, malformed output, EOF/worker loss, RPC errors and tool-level
  ambiguous tool errors (including TIMEOUT, INTERNAL and DIALOG_BLOCKING)
  poison the session. Only explicit validation/policy rejections are treated
  as known failures. All tool `isError` results are preserved; callers abort
  their approved sequence on errors.
- No request retry, reconnect, backend fallback or replay is implemented.
  Killing the engine does not prove an external COM operation stopped in
  SOLIDWORKS. Marker persistence blocks both updated backends after a crash.
- **Operator recovery:** stop clients, inspect the marker and job journal,
  inspect/rebuild the synthetic part or protected copy, establish whether the
  operation happened, and resolve any pending SOLIDWORKS work/dialog. Restart
  SOLIDWORKS if the native outcome cannot otherwise be established. Only then
  archive the journal and manually remove the exact marker for that CAD user.
  A fresh session must inspect state before planning new work; do not replay
  an approved sequence merely because the marker was cleared.

Disposal cancels and drains active dispatch preparation before releasing the lease.
If process termination cannot be confirmed, it throws while retaining the lease
and durable quarantine; stop the worker and call Dispose again to finish cleanup.

## Integration and roadmap

The application integration branch supplies `UpstreamBackendJob`, a local
whole-job facade with no legacy-executor fallback. Build its Core project
with `-p:CadEngineRoot=<this checkout>`; ordinary builds exclude the facade
and preserve the legacy path. See the application's `docs/UPSTREAM-ENGINE.md`
for exact coordination, approval, journalling, ownership and artifact hooks.

1. Run the real Windows probe, naming live test and plate scenario; retain
   native evidence before admitting an upstream job in production.
2. Persist backend identity and approved discovered-tool plan/schema hash in
   JobRevision. Dispatch once at JobCoordinator's job boundary; route upstream
   before acquiring the legacy lease, because the upstream facade owns it.
3. Bind engine document handles to managed-model identities; extend existing
   protected-copy/checkpoint and uncertain-attempt records before saved-model
   edits. Keep approvals, readback checks and artifact authorization in the app.
4. Admit additional upstream families by evidence: constraints/selection and
   inspection first, then features, assemblies/drawings, sheetmetal/weldments,
   export and licensed simulation. Distinguish discovered, exercised and
   native-verified capabilities in job/UI metadata.
5. Stop new legacy feature ports. Retire overlapping legacy handlers family by
   family after acceptance parity; retain legacy workflows behind explicit
   backend selection until their document/verification contracts migrate.
6. Evaluate selected Project B constraint diagnostics and reconstruction
   algorithms as isolated licensed modules after the execution boundary is
   proven. Do not adopt its second engine or heavy ML dependencies now.
