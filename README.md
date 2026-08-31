# Zashboard for Windows

Zashboard for Windows is a native WinUI 3 control center for Clash-compatible controllers. It targets .NET 10 and Windows App SDK 2.4, follows Fluent design conventions, and treats Native AOT as a release requirement.

The first release controls an existing local or remote controller. Bundling a core, changing the Windows system proxy, installing a service, and managing TUN privileges are intentionally outside the GUI process and will be added behind a separate service boundary.

## Technology baseline

- .NET SDK 10.0.400 and C# 14
- Windows App SDK 2.4.0 and WinUI 3
- Windows Community Toolkit 8.2.251219 feature packages
- CommunityToolkit.Mvvm 8.4.2
- WinUIEx 2.9.3
- Single-project MSIX for x64 and ARM64
- Native AOT, full trimming, and self-contained .NET runtime in Release

Package versions are centralized in `Directory.Packages.props`. The SDK is pinned by `global.json`.

## Projects

```text
src/Zashboard.App             WinUI shell, pages, view models, window and tray integration
src/Zashboard.Core            Transport-neutral Clash models, contracts and capability state
src/Zashboard.Infrastructure  REST, WebSocket, source-generated JSON and protected persistence
eng/smoke/Zashboard.NativeAot.Smoke  CI runtime proof for DI, JSON, REST and DPAPI under Native AOT
tests/Zashboard.Core.Tests
tests/Zashboard.App.Logic.Tests
tests/Zashboard.Infrastructure.Tests
```

The API and stream clients are session-bound. Switching backends cancels the previous session, increments a monotonic epoch, clears volatile state, and rejects late writes from the old epoch.

## Current feature surface

- Multiple controller profiles with DPAPI-protected credentials
- Overview with mode and capability-aware TUN controls, live and cumulative traffic, memory, recent connection rates, and response-driven runtime statistics
- Proxy groups, node selection, group and node delay tests, provider updates and health checks
- Smart group ranks, weights, reset, and per-connection block actions exposed by response data
- Live connections, rules and rule providers, bounded incremental logs, arbitrary DNS record queries, cache maintenance, listener configuration, configuration reload/import, configurable latency probes, Geo data update, core restart, and core upgrade
- Fluent shell, Mica, AOT-safe persisted window placement, standalone WinUIEx tray behavior, startup task, light/dark/system themes, keyboard access keys, and accessibility metadata

Dashboard storage and dashboard upgrade endpoints are implemented in the compatibility client but intentionally have no native UI. They are browser-dashboard concerns rather than Windows client state.

## Development

Install Visual Studio 2026 with the WinUI application development and Desktop development with C++ workloads, the Windows 11 SDK, and .NET SDK 10.0.400.

```powershell
dotnet restore Zashboard.slnx
dotnet build src/Zashboard.App/Zashboard.App.csproj -c Debug -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false
dotnet test tests/Zashboard.Core.Tests/Zashboard.Core.Tests.csproj -c Release
dotnet test tests/Zashboard.App.Logic.Tests/Zashboard.App.Logic.Tests.csproj -c Release
dotnet test tests/Zashboard.Infrastructure.Tests/Zashboard.Infrastructure.Tests.csproj -c Release
```

The generated app assets are reproducible:

```powershell
./eng/Generate-AppAssets.ps1
```

## Native AOT release

Each architecture is published independently. Release explicitly disables ReadyToRun so that it cannot be mistaken for Native AOT.

```powershell
dotnet publish src/Zashboard.App/Zashboard.App.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
dotnet publish src/Zashboard.App/Zashboard.App.csproj -c Release -r win-arm64 -p:Platform=ARM64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

`eng/Assert-NativeAot.ps1` verifies that a loose application executable has no CLR header, exports the Native AOT `DotNetRuntimeDebugHeader`, contains no managed host runtime, leaves no managed entry assembly, and matches its expected processor architecture. `eng/Assert-MsixNativeAot.ps1` independently unpacks the generated MSIX, applies the same Native AOT proof to the manifest entry executable, checks the processor architecture, and confirms that the CI artifact is unsigned. Release keeps `DebuggerSupport` enabled as an explicit part of this verification contract. CsWinRT AOT warnings run at level 2, unsafe vtable generation is enabled, and every managed type that crosses a WinRT interface boundary remains source-generator compatible. CI runs both checks for x64 and ARM64, and also executes an x64 Native AOT smoke process that exercises dependency injection, profile persistence, Clash JSON callbacks, REST serialization, capability observation, a real loopback WebSocket handshake and stream payload, and CurrentUser DPAPI. Unsigned CI packages are artifacts only; installable releases must be signed with a publisher matching `Package.appxmanifest`.

The current baseline was verified on Windows on 2026-09-01. The Core suite passes 47 tests, the application logic suite passes 80, and the Infrastructure suite passes 153, for 280 passing tests including the two Windows-only DPAPI confidentiality and tamper-detection cases. The Debug WinUI project compiles without warnings. The x64 Release output passes the loose executable and unpacked MSIX Native AOT checks. The x64 package was development-registered and exercised against an authenticated Clash controller: the Overview, Connections, and Logs live views remained populated during high-frequency incremental updates; proxy groups rendered in a responsive two-column flow with in-card secondary-click latency tests; rule columns and on-demand details were reviewed; and the backend quick selector and management entry point were exercised. ARM64 packaging remains configured, but this host cannot reverify the current ARM64 Native AOT output because its C++ ARM64 linker tools are not installed.

## Security

- Backend secrets are excluded from profile JSON and encrypted with CurrentUser DPAPI.
- REST uses the `Authorization: Bearer` header. WebSocket uses the Clash-compatible `token` query parameter.
- Diagnostic URIs and error bodies are redacted before they leave the transport layer.
- TLS certificate validation cannot be disabled by the application.
- The GUI runs without administrator privileges. Future privileged operations belong in a separate service.

## API compatibility

Compatibility is based on observed endpoint behavior rather than a single core version string. Optional capabilities have `Unknown`, `Supported`, and `Unsupported` states. Successful calls and response data establish support; authentication, timeouts, and transient server failures do not downgrade a capability.

See [docs/api-compatibility.md](docs/api-compatibility.md) and [docs/architecture.md](docs/architecture.md) for the detailed contracts.
