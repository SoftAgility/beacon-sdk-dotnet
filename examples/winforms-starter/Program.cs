namespace InventoryManager;

/// <summary>
/// Application entry point for the un-instrumented Inventory Manager starter.
///
/// This is the "before" state for the Beacon "add usage tracking in 30 minutes"
/// tutorial: a complete, working WinForms app with NO analytics. Follow the
/// tutorial to add the SoftAgility.Beacon SDK and instrument it yourself.
///
/// The finished, fully-instrumented version of this same app lives in
/// ../winforms — use it as a reference if you get stuck.
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // TUTORIAL — Step 2: configure the Beacon SDK here, before Application.Run.
        // TUTORIAL — Step 5: wire AppDomain.CurrentDomain.UnhandledException and
        //                    Application.ThreadException to TrackException here.

        Application.Run(new MainForm());
    }
}
