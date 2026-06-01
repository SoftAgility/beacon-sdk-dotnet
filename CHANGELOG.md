# Changelog

All notable changes to the SoftAgility Beacon .NET SDK are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
