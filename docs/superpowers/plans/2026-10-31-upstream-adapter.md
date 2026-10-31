# Upstream Adapter Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for inline implementation and a final independent review.

**Goal:** Full upstream foundation plus an opt-in reusable MCP job boundary.
**Architecture:** .NET Standard stdio client; upstream .NET 10 runtime; shared CAD lease and durable uncertain-outcome marker.
**Tech Stack:** C#, Newtonsoft.Json 13.0.3, .NET Standard 2.0, .NET 10 console acceptance tests.
**Spec:** ../specs/2026-10-31-upstream-adapter-design.md

## Global Constraints

- No SOLIDWORKS 2020 gate, proprietary binary redistribution, remote arbitrary tool endpoint, automatic replay or backend interleaving.
- Preserve full upstream modules, licence and tests; custom work on integration branches, no custom PR merge.

## Review Focus

- Cancellation queued behind another call must not cancel the active call.
- Transport loss after dispatch must block later sessions and the legacy lease.
- A server-reported TIMEOUT is uncertain even though its JSON-RPC response arrived.
- Journalling failure must prevent unrecorded dispatch and keep a marker after a dispatched call.
- Discovery pagination, worker stderr, unexpected EOF and wrong protocol versions must be bounded and handled.

## Task 1: Baseline
- [x] Inspect all repositories and scripts; audit licence and target branch.
- [x] Import the entire 185-file upstream snapshot pinned at 05f3682 and correct false prior provenance.
- [x] Run upstream repository guard and offline suite; record Linux limitations.

## Task 2: Reusable adapter
Files: adapters/SolidWorksCadEngine.Adapter/{EngineOptions,FileCadExecutionBoundary,McpBackendSession}.cs;
tests/SolidWorksCadEngine.AdapterTests/Program.cs.
Interfaces: StartAsync(options, boundary, cancellation); Tools; CallToolAsync(name, arguments, cancellation); Dispose.
- [x] Add subprocess fixture tests for negotiation, paginated discovery, raw results/errors, policy and failure modes. Watch RED.
- [x] Implement generic client and crash-safe job boundary. Run the console test project to GREEN.
- [x] Add adapter CI and Windows acceptance runner; commit.

## Task 3: Existing application boundary
Files: Core/Backends/UpstreamBackendJob.cs; Core/Remote/CadNativeControlLease.cs; build/SolidWorks2020Interop.targets;
tests/SolidWorksCadAgent.BackendTests; docs/UPSTREAM-ENGINE.md.
Interfaces: local opt-in whole-job facade, CadEngineRoot MSBuild reference; same lease/recovery marker as Task 2.
- [x] Test shared lease exclusion, marker blocking and settings opt-in; watch RED then implement.
- [x] Relax version-28 hardcoded compile guard but retain missing/mismatched assembly validation.
- [x] Run available application checks; document coordinator integration points without registering a remote endpoint.

## Task 4: Acceptance and delivery
- [x] Build/run protocol tests and available upstream/app suites; attempt full engine build and report exact blockers.
- [x] Provide synthetic 120x60x8, diameter-8 hole at sketch (20,10), width-150 edit acceptance commands and oracles.
- [x] Independent final review; fix substantive findings with regression checks.
- [x] Push separate integration branches, create PRs and record evidence/rollout roadmap. Do not merge custom work.

Native acceptance is a documented Windows follow-up, not a completed native run. Local whole-job facade is supplied but not routed through production coordinator/UI. See TEST-RESULTS.md for evidence and explicit limits.
