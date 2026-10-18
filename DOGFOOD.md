# Axial 0.11 / Glue 0.2 / Reified 0.12 dogfood (in progress)

## Findings

- **[observed]** `src/LogDug.Core/Files/Files.fs`: opening `Axial.FileSystem` introduces `FileChange`/`FileChangeKind` names which collide with the app's own change model; the compiler inferred the wrong record even with an annotated queue. Suggest qualifying the names in integration examples and documenting the collision.
- **[verified]** `src/LogDug.Core/Runtime.fs`: Axial.Guardrails 0.11 flags the existing synchronous stop/handoff and `TimeZoneInfo.Local`. Suggest documenting an `ElmishRuntime` migration recipe showing `StopAndWait` and a host-supplied time zone.
- **[verified]** `src/LogDug.Core/Files/Files.fs`: guardrails reject the existing direct `FileSystemWatcher`. Suggest an `Axial.Files.watch` migration example including conversion to application-specific change events.
- **[observed]** `src/LogDug.Core/LogDug.Core.fsproj`, `src/LogDug.Core/App.fs`, `src/LogDug.Core/FlowCmd.fs`: adding the `Axial.Elmish` reference introduces a namespace collision with `Elmish`; partially qualified `open Elmish` resolves to `Axial.Elmish`, and inference of records with an `EnvironmentVariables` field changes. Suggest a migration note recommending `open global.Elmish` and explicit record type annotations.
- **[observed]** `src/LogDug.Core/Files/Files.fs`: switching the watcher to `Axial.Files.watch` requires an explicit `Axial.Files` package reference; the version in central package management alone does not expose its module. Suggest a package installation note alongside the API example.

- **[verified]** `AXIAL_GUARDRAILS_SEVERITY=warning` does not override severity for library consumers (upstream bug recorded separately). Use the MSBuild global property `dotnet build LogDug.slnx -c Release -p:AxialGuardrailsSeverity=warning`; AXG001 is reported as warnings with this property.

## Verification

- `dotnet build LogDug.slnx -c Release -p:AxialGuardrailsSeverity=warning`: AXG001 downgraded to warnings; build exposed FS0960 in App.axaml.fs (member before let), corrected in worktree. Host time-zone read moved to Program.fs with an explicit clock boundary marker.
- Tests: not run (build blocked).
- Headless screenshot: not run.
- `--self-test`: not run.
- NativeAOT publish and self-test: not run.
