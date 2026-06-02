# Inventory Manager — starter (un-instrumented)

A small, complete WinForms inventory app with **no analytics**. This is the
*starting point* for the tutorial **"How to add usage tracking to a .NET
application in 30 minutes."** You clone it, then add the
[SoftAgility Beacon](https://github.com/softagility/beacon-sdk-dotnet) SDK yourself, step by step.

The fully-instrumented version of this same app (the "after") lives in [`../winforms`](../winforms). Use it as a reference if you get stuck.

## What it does

- Add / edit / delete inventory items
- Search / filter the list
- Export to CSV
- An About dialog

All in-memory; nothing is persisted. The point is to have realistic user
actions to instrument.

## Run it

This starter targets `net8.0-windows`, so running it as-is needs the **.NET 8
SDK** on **Windows**. That's only this sample's choice, **not** a Beacon
requirement: as of 3.2.0 the SDK runs on **.NET Framework 4.8**, **.NET Standard
2.0**, and **.NET 6 through .NET 10**. If your real app is on a different
framework (say a legacy WinForms app on .NET Framework 4.8), retarget this
project's `<TargetFramework>` to match it and instrument that instead.

```bash
dotnet run
```

You should get a window titled **Inventory Manager** with five demo rows.

## Where the tutorial plugs in

The source is annotated with `// TUTORIAL — Step N:` comments at exactly the
points where each step of the walkthrough adds code:

| Step | File | What you add |
|------|------|--------------|
| 1 | `InventoryManager.csproj` | `dotnet add package SoftAgility.Beacon` |
| 2 | `Program.cs` | `BeaconTracker.Configure(...)` on startup |
| 3 | `MainForm.cs` (ctor + `MainForm_Load`) | `Identify(...)` and `StartSession()` |
| 4 | `MainForm.cs` (button handlers) | `Track(...)` calls for add / export / navigation |
| 5 | `Program.cs` + `MainForm.cs` (`BtnExport_Click` catch) | `TrackException(...)` |
| 6 | `MainForm.cs` + `MainForm.Designer.cs` | a `FormClosing` handler that disposes the tracker |

Follow the post, fill in those spots, run it again, and watch the events arrive
in your Beacon dashboard.

> **Don't forget:** the `Product` value you set in Step 2 must match a product
> **slug you've registered** in the Beacon portal, or your events are rejected
> as `UNRECOGNIZED_PRODUCT` and never show up. Register the product first.
