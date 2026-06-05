# Changelog

All notable changes to the SoftAgility Beacon .NET SDK are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [3.3.0] - 2026-06-04

### Added

- **Durable session-end on shutdown — clean app closes are no longer lost.** Previously, closing the app or calling `EndSession()` sent the session end fire-and-forget, so the request was usually abandoned when the process exited; the server only closed the session via its inactivity timeout (recorded as a `timeout`, up to a day later). Now the SDK persists a self-contained session-end record (session id, actor, product/version, started-at, account/license, ended-at) to a local SQLite store **before** shutdown completes, then makes a best-effort immediate send. Anything not delivered at shutdown is sent on the **next launch** as an `sdk_recovery` end carrying the *original* end time — so session duration and completion are recorded faithfully across offline closes, crashes, and force-kills. Multiple sessions ended in one run are each delivered. (The recovery/supersede behavior requires the matching backend support; a live `normal` end while online works against the existing endpoint.)
- **`BeaconTracker.EndSessionAsync()`** — awaitable session end. `tracker.EndSession(); await tracker.FlushAsync();` (or `await tracker.EndSessionAsync();`) now guarantees the end is delivered when online; `FlushAsync()` also drains any pending session-end records. Added as a concrete method on `BeaconTracker` — `IBeaconTracker` is unchanged, so this is additive and SemVer-minor safe.
- **`BeaconOptions.ShutdownFlushTimeout`** (default 2 seconds; `TimeSpan.Zero` to skip the blocking send and persist-only) — bounds the best-effort send during `Dispose`.

### Changed

- `EndSession()` is no longer a lossy fire-and-forget — it is backed by the durable store, so an end is never dropped because the process exited mid-request.
- `OptOut()` and `Reset()` now purge any pending session-end records (consent-respecting — pending ends are not delivered after opt-out).

## [3.2.1] - 2026-06-02

### Changed

- **Docs:** README now documents the full target-framework matrix introduced in 3.2.0 (.NET Framework 4.8 / .NET Standard 2.0 / .NET 6+). No code change — the 3.2.0 and 3.2.1 binaries are identical; this release exists only to surface the corrected README on NuGet (the README is embedded in the package, and 3.2.0 shipped with the old `.NET 8` text).

## [3.2.0] - 2026-06-02

### Added

- **Broadened target frameworks (non-breaking).** The package now multi-targets `net8.0;net8.0-windows;net6.0;netstandard2.0;net48`, so it can be referenced from **.NET Framework 4.8**, **.NET Standard 2.0**, **.NET 6/7**, and modern **.NET 8/9/10** — in addition to the previous `net8.0`/`net8.0-windows`. No public-API or wire-format change; existing .NET 8 consumers are unaffected. Down-level targets use internal compatibility shims (System.Text.Json 8.0 for `SnakeCaseLower`, PolySharp for `Index`/`Range`/`init`, `Microsoft.Bcl.AsyncInterfaces`, `System.Memory`). Verified on all five TFMs; .NET Framework SQLite native loading proven via net48/net472 consumer smoke tests.

## [3.1.0] - 2026-06-01

### Changed

- **Disk queue hardening (non-breaking).** The SQLite offline queue now sets `PRAGMA busy_timeout=5000`. When multiple `BeaconTracker` instances share the same `Product` on one host — e.g. the same app running as several processes, or a multi-worker server — a contending writer now waits up to 5s for the write lock instead of failing immediately with `SQLITE_BUSY` (the default timeout is 0). The default rollback journal is intentionally kept rather than WAL, so the main `.db` file keeps reflecting the queue's true size for the `MaxQueueSizeMb` cap. Off the user-facing path: `Track()` is non-blocking (in-memory queue); only the background flush thread touches the disk queue.

## [3.0.0] - 2026-06-01

### Changed

- **BREAKING:** Renamed `BeaconOptions.AppVersion` → `ProductVersion`. The wire field `source_version` is now `product_version` on events, session starts, exceptions, the actor-identify payload, and the exported event manifest. The value that flows is unchanged (still the application version string) — only the field/key name changed. The `Product` / `product` app-identity field is unaffected. Consumers must rename `options.AppVersion = ...` to `options.ProductVersion = ...` (and `"AppVersion"` → `"ProductVersion"` in any `appsettings.json` Beacon section) when upgrading.

## [2.0.0] - 2026-05-31

### Changed

- **BREAKING:** Renamed `BeaconOptions.AppName` → `Product`. The wire field `source_app` is now `product` on events, session starts, exceptions, the actor-identify payload, and the exported event manifest. The value that flows is unchanged (still the registered product slug) — only the field/key name changed. `AppVersion` / `source_version` are unaffected. Consumers must rename `options.AppName = ...` to `options.Product = ...` (and `"AppName"` → `"Product"` in any `appsettings.json` Beacon section) when upgrading.

## [1.1.0] - 2026-05-26

### Added

- **Account and license context.** New `SetAccount(string)` / `ClearAccount()` / `SetLicense(string)` / `ClearLicense()` methods on `IBeaconTracker` carry first-class B2B account and license identifiers through every event, session start, and exception report. Account/license context is stored in-process under the existing `_sessionLock`, applied to every subsequent payload until cleared or `Reset()` is called, and validated for 1-256 chars + no control characters (matching the backend `EventValidator`). When no context is set, the `account_id` / `license_id` JSON fields are OMITTED entirely from the payload rather than serialized as null — the backend distinguishes "not present" from "present but invalid". `Reset()` now clears account and license context in addition to actor identity and queued events.

### Fixed

- **Main `.csproj` no longer compiles the `examples/` directory.** `SoftAgility.Beacon.csproj` now excludes `examples/**` from `DefaultItemExcludes` (it already excluded `tests/**`). The recent `examples/winforms/` addition was being pulled into the library compile, producing a wall of "Type 'Label' could not be found" errors on a clean build. The example project has its own `BeaconExample.InventoryManager.csproj` and was never meant to be part of the SDK assembly.

## [1.0.1] - 2026-05-08

### Removed

- **Init-time preflight check.** The SDK previously fired an empty-batch POST to `/v1/events` on first `Track()` / `StartSession()` to detect a 401 response from a bad API key before any real events were queued. With the observability lift below, a wrong API key now surfaces naturally on the first real flush via the configured `ILogger`. Removing the preflight aligns the .NET SDK with the JS SDK (which never had it) and the rest of the analytics-SDK industry. Eliminates one HTTP request per app start and the spurious 400 `batch_empty` log noise on the backend.

### Added

- **Response body in error logs.** `BeaconHttpClient` now reads the response body on every non-2xx HTTP response and includes the truncated body (max 500 chars) in the warning/error message. Customers running into "your event was rejected" now see the structured backend reason — e.g. `"Product 'foo' is not registered"`, `"API key rejected"`, `"Account hard-capped"` — rather than just the bare HTTP status code. Applies to all four ingest paths (events / sessions / sessions-end / exceptions / actors-identify).
- **Session start/end + identify error logging.** All four endpoints previously returned `bool` for non-2xx responses without surfacing the backend reason via the configured `ILogger`. They now log a Warning with status code + truncated response body. Mirrors the C++ SDK's observability behaviour.

## [1.0.0] - 2026-05-06

### Added

- Initial public release
- `IBeaconTracker` interface with `Identify`, `Track`, `StartSession`, `EndSession`, `FlushAsync`, `TrackException`, `OptOut`, `OptIn`, `Reset`, `ExportEventManifest`
- `BeaconOptions` configuration with `ApiKey`, `ApiBaseUrl`, `AppName`, `AppVersion`, plus tunables for flush cadence, batch size, disk-queue cap, breadcrumb buffer
- `ServiceCollectionExtensions.AddBeacon(...)` for `Microsoft.Extensions.DependencyInjection` integration
- Anonymous-by-default device identity with deterministic linking on `Identify`
- SQLite-backed durable event queue with exponential-backoff retry
- Breadcrumb ring buffer auto-attached to exception reports
- Opt-in / opt-out persistence to disk
- Event manifest export for upload to the portal's Allowlists Import flow
- Targets `net8.0` and `net8.0-windows`
