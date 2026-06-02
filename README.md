# SoftAgility Beacon — .NET SDK

[![NuGet](https://img.shields.io/nuget/v/SoftAgility.Beacon.svg)](https://www.nuget.org/packages/SoftAgility.Beacon)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

The official .NET SDK for [SoftAgility Beacon](https://beacon.softagility.com) — a usage-tracking SaaS for desktop and server .NET applications. Send events, manage sessions, capture exceptions, and queue events offline. Buffered, batched, durable, and safe to call from any thread.

---

## Installation

```bash
dotnet add package SoftAgility.Beacon
```

Runs almost anywhere: the package multi-targets **.NET 6/7/8/9/10**, **.NET Framework 4.8**, and **.NET Standard 2.0** (with a Windows-specific target for WinForms desktop apps). Whether you ship a legacy WinForms app on the .NET Framework or a modern .NET 10 service, the integration is identical and the wire format is the same.

---

## Quick start (with `Microsoft.Extensions.DependencyInjection`)

```csharp
using SoftAgility.Beacon;

var services = new ServiceCollection();

services.AddBeacon(options =>
{
    options.ApiKey      = "pk_your_api_key";
    options.ApiBaseUrl  = "https://api.beacon.softagility.com";
    options.Product     = "MyApp";       // the registered product; sent as the `product` field on every event
    options.ProductVersion = "1.4.2";    // becomes product_version on every event

    // Optional — declare the events your app emits, then export the manifest
    // for upload to the portal's Allowlists > Import page.
    options.Events
        .Define("auth", "user.signed-in")
        .Define("billing", "checkout.completed")
        .Define("ui", "dashboard.viewed");
});

var provider = services.BuildServiceProvider();

// Resolve once and use as a singleton — thread-safe
var beacon = provider.GetRequiredService<IBeaconTracker>();

// Identify the current user (does NOT block; sets actor id for subsequent calls)
beacon.Identify("user-12345");

// Track an event
beacon.Track("billing", "checkout.completed", new { amount = 1990, currency = "USD" });

// Start / end a session
beacon.StartSession();
// ... user does things ...
beacon.EndSession();

// Report an unhandled exception (fire-and-forget; never throws)
try { /* risky work */ }
catch (Exception ex) { beacon.TrackException(ex, ExceptionSeverity.NonFatal); }

// On shutdown, flush any buffered events
await beacon.FlushAsync();
```

## Quick start (without DI)

```csharp
var options = new BeaconOptions
{
    ApiKey     = "pk_your_api_key",
    ApiBaseUrl = "https://api.beacon.softagility.com",
    Product    = "MyApp",
    ProductVersion = "1.4.2",
};

await using var beacon = new BeaconTracker(options);

beacon.Identify("user-12345");
beacon.Track("auth", "user.signed-in");
```

`BeaconTracker` is `IAsyncDisposable` — `await using` ensures any buffered events flush during disposal.

---

## What the SDK does for you

| Behaviour | Detail |
|---|---|
| **Buffered & batched** | Events are queued in memory and flushed every `FlushIntervalSeconds` (default 60) in batches of `MaxBatchSize` (default 25). Background, non-blocking. |
| **Durable on disk** | Failed sends spill into a SQLite-backed disk queue (`MaxQueueSizeMb`, default 10 MB). Retries with exponential backoff. Survives process restarts. |
| **Thread-safe** | All public methods are safe to call concurrently. The tracker is registered as a singleton. |
| **Anonymous-by-default** | If you don't `Identify`, the SDK generates and reuses an anonymous device ID. Calling `Identify` later links the anonymous device to the identified actor (POST `/v1/actors/identify`). |
| **Exception capture** | `TrackException(ex)` enriches with stack trace, OS info, and a configurable breadcrumb buffer (`MaxBreadcrumbs`, default 25 — every `Track` call drops a breadcrumb). |
| **Sessions** | `StartSession` / `EndSession` send dedicated lifecycle events the portal stitches into session reports. |
| **Opt-in / Opt-out** | `OptOut()` persists the choice to disk and turns all subsequent calls into no-ops; `OptIn()` resumes. Idempotent and never throws. |
| **Reset** | `Reset()` clears identity + session + queue and rotates the anonymous device ID — used when a user logs out, or for "Forget me" UX. |
| **Disabled mode** | `BeaconOptions.Enabled = false` disables every method as a silent no-op. Useful for local dev. |

---

## Configuration reference

| Option | Default | Range | Notes |
|---|---|---|---|
| `ApiKey` | required | — | API key from the Beacon portal. Sent as `Authorization: Bearer`. |
| `ApiBaseUrl` | required | — | Backend base URL. Trailing slash normalised. |
| `Product` | required | ≤128 chars | The registered product; sent as the `product` field on every event. Must match a registered product in the portal. |
| `ProductVersion` | required | ≤256 chars | Becomes `product_version`. Auto-registers on first event. |
| `Enabled` | `true` | — | When false, every public method is a silent no-op. |
| `FlushIntervalSeconds` | `60` | 1-3600 | Background flush cadence. |
| `MaxBatchSize` | `25` | 1-1000 | Events per HTTP batch. |
| `MaxQueueSizeMb` | `10` | 1-1000 | SQLite disk-queue cap. |
| `MaxBreadcrumbs` | `25` | 0-200 | Breadcrumb ring-buffer size. 0 disables breadcrumbs. |
| `Events` | `EventDefinitionBuilder` | — | Declarative event registry; export via `ExportEventManifest`. |
| `Logger` | `null` | — | Optional `Microsoft.Extensions.Logging.ILogger` for SDK diagnostics. |

---

## Compatibility

As of **3.2.0**, the package ships five target frameworks:

- **`net8.0`** — base library; consumed by .NET 8, 9, and 10 (any platform)
- **`net8.0-windows`** — same library with `UseWindowsForms = true`, for WinForms desktop apps on modern .NET
- **`net6.0`** — for .NET 6 and 7 apps
- **`netstandard2.0`** — for .NET Standard 2.0 consumers (older .NET Core, Mono, Unity, mixed legacy libraries)
- **`net48`** — for **.NET Framework 4.8** apps (WinForms/WPF/console on the classic framework)

NuGet's automatic best-match resolution picks the right TFM at install time — no manual selection needed. (3.0.0–3.1.0 shipped `net8.0`/`net8.0-windows` only; **3.2.0** broadened the matrix with no public-API or wire-format change.)

---

## Examples

A working WinForms example app lives at [`examples/winforms/`](examples/winforms/) — covers DI registration, Identify, Track, sessions, exception reporting, and graceful shutdown. References `SoftAgility.Beacon` via NuGet (1.0.1), so it builds standalone after `dotnet restore`.

---

## Related

- **Beacon portal:** https://beacon.softagility.com
- **Other SDKs:** [JavaScript / TypeScript](https://www.npmjs.com/package/@softagility/beacon-js), [C++](https://github.com/softagility/beacon-sdk-cpp)
- **REST API:** integrate without an SDK by POSTing to `/v1/events` directly
- **Source:** https://github.com/softagility/beacon-sdk-dotnet

## License

MIT — see [LICENSE](LICENSE).
