# Axial 0.11 / Glue 0.2 / Reified 0.12 dogfood (in progress)

## Findings

- **[observed]** `src/LogDug.Core/Files/Files.fs`: opening `Axial.FileSystem` introduces `FileChange`/`FileChangeKind` names which collide with the app's own change model; the compiler inferred the wrong record even with an annotated queue. Suggest qualifying the names in integration examples and documenting the collision.
- **[verified]** `src/LogDug.Core/Runtime.fs`: Axial.Guardrails 0.11 flags the existing synchronous stop/handoff and `TimeZoneInfo.Local`. Suggest documenting an `ElmishRuntime` migration recipe showing `StopAndWait` and a host-supplied time zone.
- **[verified]** `src/LogDug/Program.fs`: the host's `TimeZoneInfo.Local` read requires `// axial-allow-effect: environment`, not `clock`; the latter causes stale-suppression AXG002.
- **[observed]** `RenderTests.message tokens get tones`: test run reports a single Plain token instead of the expected property-key token (57/58 passed); investigate separately.
- **[verified]** `src/LogDug.Core/Files/Files.fs`: guardrails reject the existing direct `FileSystemWatcher`. Suggest an `Axial.Files.watch` migration example including conversion to application-specific change events.
- **[observed]** `src/LogDug.Core/LogDug.Core.fsproj`, `src/LogDug.Core/App.fs`, `src/LogDug.Core/FlowCmd.fs`: adding the `Axial.Elmish` reference introduces a namespace collision with `Elmish`; partially qualified `open Elmish` resolves to `Axial.Elmish`, and inference of records with an `EnvironmentVariables` field changes. Suggest a migration note recommending `open global.Elmish` and explicit record type annotations.
- **[observed]** `src/LogDug.Core/Files/Files.fs`: switching the watcher to `Axial.Files.watch` requires an explicit `Axial.Files` package reference; the version in central package management alone does not expose its module. Suggest a package installation note alongside the API example.

- **[verified]** `AXIAL_GUARDRAILS_SEVERITY=warning` does not override severity for library consumers (upstream bug recorded separately). Use the MSBuild global property `dotnet build LogDug.slnx -c Release -p:AxialGuardrailsSeverity=warning`; AXG001 is reported as warnings with this property.

## Verification

- `dotnet build LogDug.slnx -c Release -p:AxialGuardrailsSeverity=warning`: AXG001 downgraded to warnings; build exposed FS0960 in App.axaml.fs (member before let), corrected in worktree. Host time-zone read moved to Program.fs with an explicit clock boundary marker.
- `dotnet build LogDug.slnx -c Release -p:AxialGuardrailsSeverity=warning --no-restore`: **passed** after correcting App.axaml.fs declaration order. Default-severity build still fails on Runtime.fs's two blocking waits; ElmishRuntime migration remains outstanding.
- `dotnet test --solution LogDug.slnx -c Release --no-build --no-restore`: **57/58 passed**; `RenderTests.message tokens get tones` fails (one Plain token instead of property-key token).
- Headless screenshot: not run.
- `dotnet run --project src/LogDug/LogDug.fsproj -c Release --no-build -- --self-test`: **10/10 passed**.
- NativeAOT publish and self-test: not run.

## Coordinator update (2026-10-19)

- verified: `AppHandle.StopAndWait(timeout)` (Axial 0.11) replaces the `Task.Run(... Stop ...).Wait` shutdown workaround in `Runtime.fs` directly; the build passes with Guardrails at error severity.
- The startup environment handoff (`ready.Task.Result`) remains, marked as a host boundary, until `ElmishRuntime.startLayer` replaces it.
- Tests: 58/58 passed on the rerun; `RenderTests.message tokens get tones` failed once earlier (possible flake, watch it). `--self-test`: 10/10 (earlier run).

