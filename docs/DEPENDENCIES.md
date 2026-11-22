# Tested dependency pins and upgrade gates (Issue #56, P0-F)

The app is deliberately one .NET Windows service, one SQLite store, one elevated
interactive USER worker and one supervisor for independently configured official tunnels. Dependency updates never
install themselves. This inventory records **build intent**; a separately
installed machine must be checked using `powershell_status` and native doctor.

| Boundary/owner | Reviewed pin or supported line | Single source / upstream | Change and rollback |
| --- | --- | --- | --- |
| Build .NET SDK | 10.0.404; exact patch, `rollForward=disable` | [`global.json`](../global.json), [Microsoft .NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | Review SDK patch, build twice, compare lock SHA. Never silently change SDK feature band or major. |
| App runtime/target | .NET 10 LTS / `net10.0-windows10.0.19041.0`; runtime 10.0.15 in accepted baseline | App csproj and self-contained `win-x64` publish | A runtime security patch requires an exact-commit new package; inspect bundled runtime and trial. Downgrade requires compatibility proof. |
| MCP protocol/SDK | ModelContextProtocol 2.4.0 | `Directory.Packages.props`, [SDK releases](https://github.com/modelcontextprotocol/csharp-sdk/releases) | Review handshake, tools/schema and cancel/error contracts before accepting. |
| NuGet hosting/SQLite | `Microsoft.Extensions.Hosting*` / `Microsoft.Data.Sqlite` 10.0.15 | `Directory.Packages.props` and four tracked graph locks | `dotnet restore --locked-mode`; test WAL, store schema and migration/downgrade safety. |
| OpenAI tunnel process | openai/tunnel-client 0.0.21; exact official ZIP and binary SHA256 | [`vendor/tunnel-client.lock.json`](../vendor/tunnel-client.lock.json), [upstream releases](https://github.com/openai/tunnel-client/releases) | Generate checked-in PS/C# constants with `Sync-VendorLock.ps1 -Update`; CI independently verifies generated files, archive, binary, manifest and trial. |
| PowerShell execution | PowerShell 7.6 LTS; currently observed 7.6.10 | Native executable identity/capability probe, [support lifecycle](https://learn.microsoft.com/powershell/scripting/install/powershell-support-lifecycle) | Manual supported LTS update; never silently install/replace PowerShell from MCP. |
| Windows/Chrome | Windows x64 with UIA/Win32/WGC; Chrome user-approved debugging | Per-call native capability/identity probes | Browser/profile/consent and secure-desktop behavior are runtime facts, never hard-coded into build pins. |
| Build actions | GitHub `checkout@v7`, `setup-dotnet@v6`, `cache@v6` | `.github/workflows/windows-acceptance.yml`, weekly reviewable Dependabot PR | Check changed action/package origin, permissions, signed artifact/digest, then CI and trial. No auto-merge. |

## Update sequence

1. Change exactly the reviewed source (`global.json`, central NuGet props, or
   vendor lock); regenerate `packages.lock.json` with that SDK only for an
   intentional NuGet update, or run `installer/Sync-VendorLock.ps1 -Update` for
   a reviewed official tunnel asset. Commit resulting graph/generated changes.
2. Windows CI pins exact SDK, checks generated vendor values, restores all four
   graphs with `--locked-mode`, builds/tests once and verifies the official
   tunnel downloaded ZIP/extracted EXE and signed-offline package SHA.
   Editing props without regenerating locks is a **failure**, not implicit
   dependency resolution. No `RestoreForceEvaluate` in acceptance.
3. Perform only the existing exact-commit `REQUEST → trial → independent live
   verification → explicit ACCEPT` operation. Failed verification leaves the
   last installed commit and rollback intact. New DB migration must be
   separately proved against active/unknown jobs before downgrade permission.
4. Record independently verified versions and runtime features; a green CI
   does not prove physical browser consent, host PowerShell path, update
   acceptance or connector tool-schema propagation.

### Generate/test locally (with pinned SDK installed)

```powershell
./installer/Sync-VendorLock.ps1
dotnet restore tests/RunnerTests/RunnerTests.csproj --locked-mode
dotnet restore src/WindowsAdminMcp/WindowsAdminMcp.csproj -r win-x64 --locked-mode
dotnet restore research/R0SdkProbe/R0SdkProbe.csproj --locked-mode
dotnet restore research/R0NativeProbe/R0NativeProbe.csproj -r win-x64 --locked-mode
```

`global.json` deliberately refuses a missing SDK: first install/review exactly
that version. Regeneration during a planned dependency update is **not** the
normal CI path. Independently rebuilding the same graph on a second Windows
runner, exercising a bad candidate update, Chrome post-restart reconnect and
full external MCP schema parity remain separate F02/F05/F06/F09/F11 gates;
they are not implied by this reproducibility slice.
