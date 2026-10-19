# Axial 0.11 / Glue 0.2 / Reified 0.12 dogfood (in progress)

## Findings

- **[observed]** `src/LogDug.Core/Files/Files.fs`: opening `Axial.FileSystem` introduces `FileChange`/`FileChangeKind` names which collide with the app's own change model; the compiler inferred the wrong record even with an annotated queue. Suggest qualifying the names in integration examples and documenting the collision. **Resolved in 0.11.0-local.2**: the types are now `FileSystemChange`/`FileSystemChangeKind` (with a new `OldPath` field), so the collision is gone.
- **[verified]** `src/LogDug.Core/Runtime.fs`: Axial.Guardrails 0.11 flags the existing synchronous stop/handoff and `TimeZoneInfo.Local`. Suggest documenting an `ElmishRuntime` migration recipe showing `StopAndWait` and a host-supplied time zone.
- **[verified]** `src/LogDug/Program.fs`: the host's `TimeZoneInfo.Local` read requires `// axial-allow-effect: environment`, not `clock`; the latter causes stale-suppression AXG002.
- **[observed]** `RenderTests.message tokens get tones`: test run reports a single Plain token instead of the expected property-key token (57/58 passed); investigate separately.
- **[verified]** `src/LogDug.Core/Files/Files.fs`: guardrails reject the existing direct `FileSystemWatcher`. Suggest an `Axial.Files.watch` migration example including conversion to application-specific change events.
- **[observed]** `src/LogDug.Core/LogDug.Core.fsproj`, `src/LogDug.Core/App.fs`, `src/LogDug.Core/FlowCmd.fs`: adding the `Axial.Elmish` reference introduces a namespace collision with `Elmish`; partially qualified `open Elmish` resolves to `Axial.Elmish`, and inference of records with an `EnvironmentVariables` field changes. Suggest a migration note recommending `open global.Elmish` and explicit record type annotations. **Resolved in 0.11.0-local.2**: the package is still `Axial.Elmish`, but its namespace is now `Axial.ElmishFlows`, so the namespace-level collision with `Elmish` is gone. The module names `Cmd` and `Sub` still collide with Elmish's modules of the same names; the app works around it with `module AxialCmd = Axial.ElmishFlows.Cmd` / `module AxialSub = Axial.ElmishFlows.Sub`.
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

## Coordinator update (2026-10-19) — Axial.ElmishFlows adoption

- Adopted the `Axial.Elmish` package (namespace `Axial.ElmishFlows`, package still `Axial.Elmish`) in `LogDug.Core`. All `FlowCmd.attempt` call sites moved to `Cmd.OfFlow.either`; `Runtime.follow` subscriptions moved to `Sub.OfFlowStream.subscribe`.
- **No `ElmishRuntime.startLayer` exists in 0.11.0-local.2**; the API is `ElmishRuntime.start options env` (and `ElmishRuntime.test env`). `Runtime.start` uses `start` with `Post` = the host's `ElmishHost.post` and `OnDefect` = a crash-path reporter.
- `Cmd.OfFlow.*` has **no fire-and-forget equivalent**: `either`/`perform`/`attempt` all dispatch a message on completion (success or failure), and the app has no no-op `Msg`. `FlowCmd.fireAndForget` is kept for `Settings.save`, whose result is deliberately discarded.
- **Argument-order footgun**: `Cmd.OfFlow.either name runtime workflow onSuccess onError` and `Sub.OfFlowStream.subscribe id runtime stream onError` take the workflow/stream positionally; piping the flow with `|>` as the last argument fails to compile. The docs example is correct but easy to get wrong.
- The Elmish runtime's root scope is not reachable for provisioning, so `Files.make`'s scope-tied Axial `Cache` could not be started there. The archive cache was rewritten as a scope-free single-flight LRU (`ArchiveCache` in `Files.fs`), and `Files` is now built eagerly via `Files.makeLive` before `ElmishRuntime.start`. The queue and hub are plain objects created with a one-shot `Flow.run`. This stops dogfooding Axial's `Cache` for the archive cache; worth a note in the Elmish guide (a `startLayer`/provisioning hook would have avoided the rewrite).
- The search pipeline runs as a never-completing `Cmd.OfFlow.perform` fiber owned by the runtime's scope (`"search-pipeline"`); its `onSuccess` is unreachable and reports a defect if it ever fires. There is no dedicated long-lived-service primitive in `Axial.ElmishFlows`.
- The `ready.Task.Result` blocking handoff and its `axial-allow-effect: blocking` marker are gone; the default-severity Guardrails build now passes with no suppressions in `Runtime.fs`.

## Verification (0.11.0-local.2)

- `dotnet build LogDug.slnx -c Release`: **passed** (default Guardrails severity, no blocking markers).
- `dotnet test --solution LogDug.slnx -c Release`: **58/58 passed**, including the headless screenshot scenario.
- `dotnet run --project src/LogDug/LogDug.fsproj -c Release --no-build -- --self-test`: **10/10 passed**.
- `git checkout -- docs/screenshots` after the screenshot test restored the checked-in screenshots.


## Verification (0.11.0-local.3)

- All six Axial packages bumped to `0.11.0-local.3` from the existing local feed. `Cmd.OfFlow.*` and `Sub.OfFlowStream.*` resolve alongside Elmish `Cmd.ofMsg` / `Sub.batch` without aliases: **no module-merge compiler finding**.
- `ElmishRuntime.startLayer` provisions `AppEnv`, queue, hub, and `Files.make` inside the runtime root scope. Archive decoding uses `Cache.makeBounded` (12 entries, single-flight, failures not retained); nested archives look up through the same cache. Startup awaits layer provisioning at the synchronous host boundary with an explicit Guardrails marker.
- `Settings.save` now uses `Cmd.OfFlow.start` after folding its typed file-system failure to unit, preserving the previous fire-and-forget behavior (no UI message and errors discarded). Removed `FlowCmd`.
- Default-severity Release build: **passed**. Solution tests: **58/58 passed on first run before deleting unused FlowCmd source**; after its removal, **57/58 passed twice**, with the previously observed `RenderTests.message tokens get tones` failure (one Plain token, expected property-key token). Published-host `--self-test` via Release `dotnet run --no-build`: **10/10 passed**. Restored `docs/screenshots` after tests.
- **[finding]** `Cmd.OfFlow.start` accepts only `Flow<'env, Never, unit>`; a side-effect with typed failures such as `Settings.save` needs explicit error handling/folding before starting.

## Verification (0.11.0-local.4)

- All Axial packages bumped to `0.11.0-local.4` from the same local feed (`~/.nuget/local-feeds/adz`). The `Axial.Elmish` package is **renamed to `Axial.ElmishFlows`** (namespace unchanged: `Axial.ElmishFlows`); only the `PackageVersion` id in `Directory.Packages.props` and the `PackageReference` id in `src/LogDug.Core/LogDug.Core.fsproj` changed. No source changes were required because the code already used `open Axial.ElmishFlows`.
- Default-severity Release build (`dotnet build LogDug.slnx -c Release`): **passed**. Only pre-existing FS0044 `BitmapEncoder` deprecation warnings in `App.axaml.fs` and `Screenshots.fs` remain; no Guardrails errors.
- Solution tests (`dotnet test --solution LogDug.slnx -c Release`): **58/58 passed**; the previously flaky `RenderTests.message tokens get tones` passed this run. Restored `docs/screenshots` after the headless screenshot scenario.
- Published-host `--self-test` via Release `dotnet run --no-build`: **10/10 passed**.
- NativeAOT publish + self-test (`bash scripts/publish-logdug.sh`, then `artifacts/publish/logdug/logdug --self-test`): **10/10 passed**. Publish emitted only the known FSharp.Core trim/AOT aggregate warnings and a non-fatal `ShadUI.OnScreenKeyboard.UIHostNoLaunch..ctor()` ILC note.

### Remaining library findings

- `Cmd.OfFlow.start` still accepts only `Flow<'env, Never, unit>`; typed-failure side effects (e.g. `Settings.save`) must fold/convert their errors to unit first.
- `Cmd.OfFlow.*` has no fire-and-forget primitive that dispatches no message on completion.
- `Axial.ElmishFlows` has no dedicated long-lived-service primitive; the search pipeline runs as a never-completing `Cmd.OfFlow.perform` fiber with an unreachable `onSuccess`.
