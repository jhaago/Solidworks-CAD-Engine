# Test evidence — 31 October 2026

Environment: Linux x64, .NET SDK 10.0.100 installed locally for this session.
No SOLIDWORKS application or proprietary API/Simulation interop assemblies
are available. Existing application main: a11982c. Upstream pin: 05f3682.

## Observed results

| Check | Result | What this establishes |
|---|---|---|
| Complete upstream tree import | 185/185 pinned files, plus provenance | All upstream service families, tests, scripts/build/CI imported; no binary redistribution |
| Upstream repository guard | Passed | Tracked files pass upstream forbidden-content check |
| Adapter .NET Standard 2.0 Release build | Passed | Reusable library compiles independently of SOLIDWORKS |
| Adapter subprocess suite | 18/18 passed | Negotiation, paginated schemas, raw content/results/errors, scripting refusal, serialization, cancellation, timeout, worker death, quarantine and lease lifecycle |
| Windows acceptance runner Release build | Passed, zero warnings/errors | Runner compiles; does not establish its native behaviour |
| App Core .NET Framework 4.8 with upstream opt-in | Passed, zero warnings/errors | Existing application can reference/build the shared adapter without moving to .NET 10 |
| App Core default legacy build | Passed, zero warnings/errors | Optional adapter reference/facade does not become a default dependency |
| Cross-backend/application boundary suite | 7/7 passed | Mutual exclusion, legacy marker refusal, manual recovery, explicit opt-in and relaxed metadata version policy |
| Existing app portable suite | 240/240 passed | Existing COM-free tests, built for installed net10.0 runtime; source project stays net8.0 |
| Unmodified upstream offline suite | 209/220 passed; 11 failed on Linux | Baseline Windows STA/path assumptions; failures retained and listed below |
| Upstream offline suite + six naming-policy cases | 215/226 passed; same 11 failed | Naming policy passes; no native renaming claim |
| Full Core/Server engine build | Blocked at upstream CheckSolidWorksInterop | SOLIDWORKS interop assemblies absent; not a successful engine build |
| Real engine MCP initialize/discovery/invocation | Not executed | Requires the actual built Windows engine; fixture success is distinct |
| Native plate, width edit, saved native evidence | Not executed | Precise Windows commands/oracles in INTEGRATION.md; no .sldprt artifact claimed |
| Native dimension_rename tool/live test | Not compiled or run | Official API checked; native acceptance remains required |

The 18 adapter cases use a real child process speaking a deterministic test
fixture protocol. They do **not** drive the upstream server binary or native
SOLIDWORKS. A controllable process supervisor holds an actual fixture child
alive to prove an unconfirmed shutdown retains CAD ownership. Malformed
valid-JSON result cases and the unconfirmed-shutdown case were observed
failing before the review fixes (16/18), then passing after them (18/18).

The `dotnet test --project ...` launcher was denied Unix-domain pipe creation
by this host's sandbox. Running the upstream test executable with `dotnet run`
provided the 220/226-case results above; no test was suppressed to obtain them.
Roslyn's optional compiler-server connection likewise cannot use that local
IPC here; the compiler falls back successfully. `UseSharedCompilation=false`
can avoid the optional connection attempt.

## Commands executed

```text
dotnet run --project tests/SolidWorksCadEngine.AdapterTests -c Release
dotnet build tools/CadEngine.Acceptance -c Release
dotnet run --project tests/SolidWorksMcp.UnitTests -p:SolidWorksDir=<absent-install>
dotnet build src/SolidWorksMcp.Server/SolidWorksMcp.Server.csproj -p:SolidWorksDir=<absent-install>
python tools/check_repo.py
```

In the application checkout:

```text
dotnet restore tests/SolidWorksCadAgent.PortableTests -p:TargetFramework=net10.0
dotnet run --project tests/SolidWorksCadAgent.PortableTests --no-restore -p:TargetFramework=net10.0
dotnet run --project tests/SolidWorksCadAgent.BackendTests -p:CadEngineRoot=<engine-checkout> -c Release
dotnet build src/SolidWorksCadAgent.Core/SolidWorksCadAgent.Core.csproj -p:CadEngineRoot=<engine-checkout> -c Release -m:1
dotnet build src/SolidWorksCadAgent.Core/SolidWorksCadAgent.Core.csproj -c Release -m:1
```

## All 11 inherited upstream failures

- PathPolicyTests.Refuses_writes_outside_the_allowed_roots
- PathPolicyTests.Relative_paths_go_under_the_output_folder
- DrawingLayoutTests.Format_families_come_from_the_file_names (Windows-path ISO fixture)
- DrawingLayoutTests.Sheet_plans_keep_what_is_left_out
- DispatcherTests.Recent_keeps_the_last_ten
- DispatcherTests.Nested_calls_run_inline_without_deadlock
- DispatcherTests.Runs_work_on_an_sta_thread_and_returns_the_result
- DispatcherTests.Exceptions_propagate_and_are_recorded
- DispatcherTests.A_call_that_cannot_start_is_dropped_with_BUSY_and_never_runs
- DispatcherTests.Cancelling_a_queued_call_drops_it
- DispatcherTests.An_overrunning_call_times_out_and_its_outcome_is_recorded_later

Dispatcher failures report Windows COM/STA unavailability on Linux. Windows
paths are parsed using Linux semantics in the other four fixtures. Windows
CI is retained; no success on Windows is inferred from this diagnosis.

## Review and remaining limits

One independent reviewer checked the adapter, application changes and native
acceptance contract against the upstream source. Two substantive issues were
fixed with regression tests: confirmed worker termination/request drain before
lease release, and fail-closed validation of tool-result envelopes before
clearing intent. The received uncertain response is now also retained in the
local journal. A final source audit also reproduced a server INTERNAL error incorrectly
clearing intent (17/18), then verified conservative quarantine for INTERNAL,
DIALOG_BLOCKING and other ambiguous tool errors. Only explicit pre-execution
validation/policy refusals are considered known failures.
No second independent review was performed after these fixes;
the full adapter and app boundary suites were rerun.

Deferred native testing: dimension-name collision and rename rollback cases
need live coverage before naming behaviour is called verified. Existing added
live coverage exercises success, driving-value edit and missing-name refusal,
but has not run. Broader app approval/checkpoint/artifact wiring is explicitly
outside this local facade prototype and remains opt-in adoption work.

CI definitions are supplied for both platforms' adapter suite and the app's
pinned shared-boundary build. CI results must be read from their runs; local
results above are not claims that remote CI passed. No software is deployed
and custom integration PRs remain unmerged.

Remote CI on initial implementation e7405f1 passed the adapter workflow on
Linux and Windows and the retained upstream Windows offline suite. The
application boundary workflow and original portable policy tests passed on
361633a; its larger Windows workflow was still running at that observation.
These are revision-specific results, not proof of native SOLIDWORKS execution.
