# Release process

A `vX.Y.Z` tag builds Logs Digger with NativeAOT on Windows, Linux and macOS, runs each shipped binary's `--self-test`, and publishes a GitHub release with:

- `logs-digger-X.Y.Z-setup-x64.exe`: per-user NSIS installer (Start menu shortcut, `logs-digger` on PATH, uninstaller; `/S` for silent)
- `logs-digger-X.Y.Z-win-x64.zip`: portable Windows build
- `logs-digger-X.Y.Z-linux-x64.tar.gz`
- `logs-digger-X.Y.Z-osx-x64.tar.gz`
- `logs-digger-X.Y.Z-osx-arm64.tar.gz`
- `SHA256SUMS.txt`

`VersionPrefix` in `Directory.Build.props` is only the local default; the tag decides the shipped version.

## Releasing

1. Optionally write `dev-docs/releases/X.Y.Z.md`. Without it, the release notes are generated from commits.
2. Check locally:
   - `dotnet build LogsDigger.slnx -c Release -m:1`
   - `dotnet test --project tests/LogsDigger.Tests -c Release`
   - `bash scripts/publish-logs-digger.sh && artifacts/publish/logs-digger/logs-digger --self-test`
3. Commit and push `main`, then `git tag vX.Y.Z && git push origin vX.Y.Z`.

To retry a failed release, use **Actions → Release → Run workflow** with the version. An existing GitHub release is kept as it is.

## What the self-test covers

`logs-digger --self-test` runs without a window and reports through its exit code. It writes a small fixture tree (plain and JSON-lines logs, and a zip inside a tar.gz) to a temp folder, starts the Axial runtime, and checks the paths that unit tests on the JIT cannot prove for a NativeAOT binary:

- archive decoding and the lazy walk;
- streamed lines;
- Reified JSON `Data` and the settings codec;
- regex highlighting;
- IANA time zones (ICU);
- the stream search pipeline.

Add a check there when a feature depends on reflection, trimming, globalization or native libraries.

## One-time setup: winget

winget packages live in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). The first version must be submitted by hand; after that, the `winget` job opens the update PR for each release.

1. Publish the first GitHub release (above).
2. Fork `microsoft/winget-pkgs` to your account. The automation pushes branches to this fork.
3. Submit the first manifest with [wingetcreate](https://github.com/microsoft/winget-create) on Windows:

   ```powershell
   winget install Microsoft.WingetCreate
   wingetcreate new https://github.com/adz/logs-digger/releases/download/v0.1.0/logs-digger-0.1.0-setup-x64.exe
   ```

   Use package identifier `AdamDavies.LogsDigger`, which must match `identifier` in `release.yml`. Use installer type `nsis`, scope `user` and license `Apache-2.0`. Let it submit the PR. Validation bots run on it, then a moderator merges it, usually within a few days.
4. Create a **classic** personal access token with `public_repo` scope and add it as repo secret `WINGET_TOKEN`. Fine-grained tokens can't open PRs against repos you don't own.
5. Add repo variable `WINGET_ENABLED` = `true`.

From then on, every non-prerelease tag submits `AdamDavies.LogsDigger` at the new version. The token expires, so renew it when winget submissions start failing.

The installer is unsigned, so Windows SmartScreen warns on first run. winget accepts unsigned installers. Azure Trusted Signing can be added to the Windows build job later.
