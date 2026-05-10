using Microsoft.Extensions.Configuration;
using SoftAgility.Beacon;

namespace BeaconExample.InventoryManager;

/// <summary>
/// Application entry point. Demonstrates how to configure the Beacon SDK
/// using the static Configure() pattern (suitable for WinForms, WPF, console apps).
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // ── Step 1: Load configuration from appsettings.json ───────────
        // In a real app, you would also add environment variables, user secrets, etc.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        // ── Step 2: Configure the Beacon SDK ───────────────────────────
        // BeaconTracker.Configure() creates a singleton instance accessible
        // via BeaconTracker.Instance. If any required field (ApiKey, ApiBaseUrl,
        // AppName) is missing or empty, the SDK disables itself silently and logs
        // a warning — it does NOT throw. The only exception Configure() throws
        // is InvalidOperationException if called a second time.
        //
        // On an enabled tracker, Identify() throws ArgumentException for null/empty/
        // oversized actor IDs; Track(cat, name) and StartSession() throw
        // InvalidOperationException if no actor has been identified; and Track(cat,
        // name, actorId) and StartSession(actorId) throw ArgumentException for
        // invalid actor IDs. On a disabled or disposed tracker, all methods are
        // silent no-ops.
        var beaconSection = configuration.GetSection("Beacon");

        BeaconTracker.Configure(options =>
        {
            options.ApiKey = beaconSection["ApiKey"] ?? string.Empty;
            options.ApiBaseUrl = beaconSection["ApiBaseUrl"] ?? string.Empty;
            options.AppName = beaconSection["AppName"] ?? string.Empty;
            options.AppVersion = beaconSection["AppVersion"] ?? "0.1.0";

            // Optional: customize flush behavior
            // options.FlushIntervalSeconds = 60;  // Default: 60 seconds
            // options.MaxBatchSize = 25;           // Default: 25 events per batch
            // options.MaxQueueSizeMb = 10;         // Default: 10 MB offline queue
            // options.Enabled = false;             // Set to false to disable tracking entirely

            // Define all events this application tracks. This registry can be
            // exported as a JSON manifest for the portal's Allowlist Import page.
            options.Events
                .Define("app", "app_closed")
                .Define("inventory", "item_added")
                .Define("inventory", "item_edited")
                .Define("inventory", "item_deleted")
                .Define("inventory", "search_performed")
                .Define("navigation", "dialog_opened")
                .Define("reporting", "csv_exported");
        });

        // ── Alternative: DI-based configuration (for ASP.NET Core / hosted apps) ──
        //
        // If your app uses Microsoft.Extensions.DependencyInjection, you can
        // register Beacon via the AddBeacon() extension method instead:
        //
        //   var builder = WebApplication.CreateBuilder(args);
        //   builder.Services.AddBeacon(options =>
        //   {
        //       options.ApiKey = builder.Configuration["Beacon:ApiKey"]!;
        //       options.ApiBaseUrl = builder.Configuration["Beacon:ApiBaseUrl"]!;
        //       options.AppName = builder.Configuration["Beacon:AppName"]!;
        //       options.AppVersion = builder.Configuration["Beacon:AppVersion"]!;
        //   });
        //
        // Then inject IBeaconTracker into your controllers or services:
        //
        //   public class MyController(IBeaconTracker tracker) : ControllerBase
        //   {
        //       public IActionResult DoSomething()
        //       {
        //           tracker.Track("feature", "action_name", userId);
        //           return Ok();
        //       }
        //   }

        // ── Step 3: Run the application ────────────────────────────────
        // Pass the tracker instance to the main form. The form calls
        // StartSession on Load and Dispose on FormClosing (which persists
        // queued events to disk for next-launch delivery and closes resources).
        Application.Run(new MainForm(BeaconTracker.Instance!));
    }
}
