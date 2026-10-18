# Axial 0.11 / Glue 0.2 / Reified 0.12 dogfood (in progress)

## Findings

- **[observed]** `src/LogDug.Core/Files/Files.fs`: opening `Axial.FileSystem` introduces `FileChange`/`FileChangeKind` names which collide with the app's own change model; the compiler inferred the wrong record even with an annotated queue. Suggest qualifying the names in integration examples and documenting the collision.
- **[verified]** `src/LogDug.Core/Runtime.fs`: Axial.Guardrails 0.11 flags the existing synchronous stop/handoff and `TimeZoneInfo.Local`. Suggest documenting an `ElmishRuntime` migration recipe showing `StopAndWait` and a host-supplied time zone.
- **[verified]** `src/LogDug.Core/Files/Files.fs`: guardrails reject the existing direct `FileSystemWatcher`. Suggest an `Axial.Files.watch` migration example including conversion to application-specific change events.

## Verification

- `dotnet build LogDug.slnx`: **fails** on the guardrail findings above; migration is incomplete.
- Tests: not run (build blocked).
- Headless screenshot: not run.
- `--self-test`: not run.
- NativeAOT publish and self-test: not run.
