using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(
        string[] args
    )
    {
        /*
         * Worker mode starts as a separate
         * process without creating a desktop window.
         */
        if (
            BridgeWorkerHost.IsWorkerMode(
                args
            )
        )
        {
            return BridgeWorkerHost
                .RunAsync(
                    args
                )
                .GetAwaiter()
                .GetResult();
        }

        /*
         * Clear the persistent worker log once per desktop Bridge launch.
         * Worker processes return above, so restarts during this desktop
         * session continue appending instead of erasing earlier diagnostics.
         */
        _ = BridgeWorkerView.TryClearLogFile();

        ApplicationConfiguration.Initialize();

        using var applicationContext =
            new DesktopBridgeApplicationContext(
                args
            );

        Application.Run(
            applicationContext
        );

        return 0;
    }
}
