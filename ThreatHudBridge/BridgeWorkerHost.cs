internal static class BridgeWorkerHost
{
    private const string WorkerArgument =
        "--worker";

    private const string StopEventArgument =
        "--stop-event";

    private const string RuntimeDirectoryName =
        "DeadlockThreatHud";

    private const string SteamAppIdFileName =
        "steam_appid.txt";

    private const string DeadlockAppId =
        "1422450";

    public static bool IsWorkerMode(
        string[] args
    )
    {
        for (
            var index = 0;
            index < args.Length;
            index += 1
        )
        {
            if (
                args[index] ==
                WorkerArgument
            )
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<int> RunAsync(
        string[] args
    )
    {
        using var view =
            new BridgeWorkerView();

        var stopEventName =
            GetArgumentValue(
                args,
                StopEventArgument
            );

        if (
            String.IsNullOrWhiteSpace(
                stopEventName
            )
        )
        {
            view.AppendLog(
                "ERROR: --stop-event is missing."
            );

            return 2;
        }

        try
        {
            PrepareSteamWorkingDirectory();

            using var stopEvent =
                EventWaitHandle.OpenExisting(
                    stopEventName
                );

            using var runtime =
                new ThreatHudBridgeRuntime(
                    GetRuntimeArguments(
                        args
                    ),
                    view
                );

            var registration =
                ThreadPool
                    .RegisterWaitForSingleObject(
                        stopEvent,

                        static (
                            state,
                            timedOut
                        ) =>
                        {
                            if (
                                !timedOut &&
                                state is
                                    ThreatHudBridgeRuntime
                                    runtimeValue
                            )
                            {
                                runtimeValue
                                    .RequestStop();
                            }
                        },

                        runtime,

                        Timeout.Infinite,

                        executeOnlyOnce:
                            true
                    );

            try
            {
                var reason =
                    await runtime.RunAsync();

                return reason switch
                {
                    BridgeRuntimeExitReason
                        .DeadlockExited =>
                            0,

                    BridgeRuntimeExitReason
                        .ShutdownRequested =>
                            1,

                    _ =>
                        2
                };
            }
            finally
            {
                registration.Unregister(
                    null
                );
            }
        }
        catch (Exception error)
        {
            view.AppendLog(
                "WORKER HOST ERROR:" +
                Environment.NewLine +
                error
            );

            return 2;
        }
    }

    private static void
        PrepareSteamWorkingDirectory()
    {
        var directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .LocalApplicationData
                ),
                RuntimeDirectoryName
            );

        Directory.CreateDirectory(
            directory
        );

        var steamAppIdPath =
            Path.Combine(
                directory,
                SteamAppIdFileName
            );

        File.WriteAllText(
            steamAppIdPath,
            DeadlockAppId
        );

        Environment.CurrentDirectory =
            directory;
    }

    private static string? GetArgumentValue(
        string[] args,
        string argumentName
    )
    {
        for (
            var index = 0;
            index < args.Length - 1;
            index += 1
        )
        {
            if (
                args[index] ==
                argumentName
            )
            {
                return args[
                    index + 1
                ];
            }
        }

        return null;
    }

    private static string[]
        GetRuntimeArguments(
            string[] args
        )
    {
        var result =
            new List<string>();

        for (
            var index = 0;
            index < args.Length;
            index += 1
        )
        {
            if (
                args[index] ==
                WorkerArgument
            )
            {
                continue;
            }

            if (
                args[index] ==
                StopEventArgument
            )
            {
                index += 1;

                continue;
            }

            result.Add(
                args[index]
            );
        }

        return result.ToArray();
    }
}