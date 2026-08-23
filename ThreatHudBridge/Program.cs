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