# Changelog

All notable changes to the SoftAgility Beacon .NET SDK are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
