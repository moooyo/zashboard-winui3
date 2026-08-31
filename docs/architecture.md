# Architecture

## Boundaries

`Zashboard.Core` contains immutable domain models and transport contracts. It has no WinUI, Windows Runtime, HTTP implementation, JSON reflection, or third-party dependency.

`Zashboard.Infrastructure` maps Clash wire payloads to Core models. All JSON operations use `ClashJsonContext`, a source-generated `JsonSerializerContext`. REST and WebSocket clients are bound to one immutable backend profile and credential for their entire lifetime.

`Zashboard.App` is the composition root. It owns the Generic Host, WinUI pages, view models, window lifecycle, tray integration, settings, and `AppSessionCoordinator`.

`Zashboard.App.Logic.Tests` links the WinUI-neutral coordinator, dispatcher, session models, and collection helper directly from production sources. This keeps session ordering, shutdown cancellation, concurrent disposal, and profile transaction tests runnable on non-Windows CI without weakening the real App project or duplicating the implementation.

## Session lifecycle

1. Load versioned backend profiles and obtain the active profile credential from CurrentUser DPAPI storage.
2. Increment `SessionEpoch`, cancel the previous lifetime token, await its stream tasks, and dispose its clients.
3. Probe `/version`, create a session-bound REST client and four stream subscriptions.
4. Load configuration, proxies, providers, rules, and rule providers concurrently.
5. Probe response-driven Smart and runtime-statistics extensions, then poll supported runtime statistics on a separate five-second timer.
6. Publish state only after checking the epoch before and on the UI dispatcher.
7. Keep log data bounded, publish log deltas to the view model, and throttle connection, traffic, and memory updates before touching UI collections.
8. Normalize per-connection counter deltas by the measured sample interval and retain both rates and cumulative totals for presentation.

The UI state is explicit: `NoBackend`, `Connecting`, `Online`, `Degraded`, `Unauthorized`, and `OfflineRetrying`. A single failed stream degrades the session without discarding the last known snapshot. Switching backends clears all volatile state immediately.

Startup and user-triggered controller mutations share one cancellable operation gate. Profile changes keep the `profile -> session` lock order through persistence and session replacement, while shutdown cancels the shared lifetime token and drains initialization, profile, refresh, and session work before disposing the host.

WinUIEx is used through its standalone `TrayIcon` and AOT-safe window extension APIs. Window placement is persisted with strongly typed `AppWindow` coordinates and `ApplicationData`; `WindowManager` is intentionally not reachable because version 2.9.3 contains a reflection-based persistence path that is incompatible with the project's warning-free Native AOT contract. The tray's hidden window remains alive until asynchronous Host disposal completes.

## Native AOT rules

- Do not add runtime-generated REST clients, runtime code generation, dynamic assembly loading, or reflection-based serialization.
- Add every wire request and response to the source-generated JSON context.
- Prefer compiled `x:Bind` and strongly typed data templates.
- Keep `AllowUnsafeBlocks` enabled so CsWinRT can generate closed generic CCW vtables.
- Keep managed classes that implement WinRT-mapped interfaces `partial`, and run `CsWinRTAotWarningLevel` at level 2.
- Treat IL2026, IL3000, and IL3050 warnings as release failures.
- Publish and verify x64 and ARM64 independently.
- Keep `PublishReadyToRun` disabled when `PublishAot` is enabled.

The Windows x64 release job also publishes and directly executes `Zashboard.NativeAot.Smoke`. The smoke proves that the native process has dynamic code disabled while exercising production dependency injection, profile persistence, Clash request and response JSON, nested deserialization validation, capability observation, a real loopback WebSocket handshake and stream mapping, and CurrentUser DPAPI. Its platform-neutral paths are also executable as a Linux Native AOT development proof, while DPAPI remains Windows-only. It supplements the full App and MSIX image checks; it does not replace packaged WinUI startup validation or ARM64 execution on native hardware.

## Persistence

Backend profile JSON contains endpoint metadata only and is written through an atomic temporary-file replacement. Credentials are stored separately in a versioned DPAPI envelope and can only be decrypted by the current Windows user.

Application preferences use packaged application local settings. Connection snapshots, traffic, and logs are intentionally not persisted.

## Future privileged service

Local core installation, process supervision, system proxy changes, TUN configuration, and elevated file operations will live in a separate Windows service or helper. The GUI will communicate through a versioned authenticated IPC contract and will remain non-elevated.
