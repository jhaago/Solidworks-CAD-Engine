# Approved upstream integration

The user's supplied architecture and autonomous implementation instructions are the specification.
Keep the complete MIT upstream C#/.NET 10 engine; retain the existing application's .NET Framework 4.8 responsibilities.
A reusable .NET Standard 2.0 client launches the existing newline-delimited MCP stdio interface, initializes,
discovers paginated tool schemas and invokes arbitrary discovered modelling families without feature wrappers.
No alternate HTTP server, COM scheduler, scripting runtime, UI rebuild or second engine.

The chosen transport is a small generic client of the existing upstream wire contract. The official .NET MCP SDK
requires a newer target than the application's framework; an out-of-process proxy would add a second RPC boundary.
.NET Standard with Newtonsoft.Json (already used by the app) keeps one direct engine boundary.

A session represents one job and owns the same FileShare.None CAD lease as the application for its entire lifetime.
Persist dispatch intent before every tools/call. Only a definite response clears the recovery marker. Transport loss,
cancellation after dispatch, timeout and server-reported uncertain outcomes poison the session and leave the marker.
Both backends refuse new jobs while that marker exists. No automatic reconnect, retry, replay or fallback.
Local operator reconciliation must establish the native outcome before clearing it. VBA is forced off and blocked.

The integration is opt-in, local and separately testable. Existing coordinator approvals, journal, checkpoints,
managed document identities and artifact delivery remain authoritative and unchanged until explicitly wired.
The prototype supplies a local whole-job facade; it is not registered as a remote route or default executor.

Windows acceptance creates a uniquely named synthetic plate, saves native evidence, checks geometry and driving
parameters, then edits the width and rebuilds in the same document. No original user document is opened.
Native behaviour is unverified until the Windows acceptance command runs. Preserve and report upstream failures.
