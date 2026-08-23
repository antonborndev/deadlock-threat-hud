using System.Text;

internal sealed class BridgeWorkerView :
    IBridgeRuntimeView,
    IDisposable
{
    private readonly object _gate =
        new();

    private readonly StreamWriter _writer;

    private bool _disposed;

    public static string LogFilePath
    {
        get
        {
            var directory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment
                            .SpecialFolder
                            .LocalApplicationData
                    ),
                    "DeadlockThreatHud"
                );

            return Path.Combine(
                directory,
                "ThreatHudBridge.worker.log"
            );
        }
    }

    public BridgeWorkerView()
    {
        var path =
            LogFilePath;

        var directory =
            Path.GetDirectoryName(
                path
            );

        if (
            !String.IsNullOrWhiteSpace(
                directory
            )
        )
        {
            Directory.CreateDirectory(
                directory
            );
        }

        var stream =
            new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite
            );

        _writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false
                )
            )
            {
                AutoFlush =
                    true
            };

        Write(
            "=== Worker process started" +
            $" | PID={Environment.ProcessId} ==="
        );
    }

    public void SetRuntimeState(
        BridgeRuntimeState state,
        string? detail = null
    )
    {
        Write(
            "STATE: " +
            state +
            (
                String.IsNullOrWhiteSpace(
                    detail
                )
                    ? String.Empty
                    : " | " + detail
            )
        );
    }

    public void SetDeadlockRunning(
        bool running
    )
    {
        Write(
            "Deadlock: " +
            (
                running
                    ? "running"
                    : "stopped"
            )
        );
    }

    public void SetSteamInitialized(
        bool initialized
    )
    {
        Write(
            "Steam API: " +
            (
                initialized
                    ? "initialized"
                    : "stopped"
            )
        );
    }

    public void SetHttpServerRunning(
        bool running,
        string address =
            "http://127.0.0.1:28741"
    )
    {
        Write(
            "HTTP API: " +
            (
                running
                    ? address
                    : "stopped"
            )
        );
    }

    public void SetAccountId(
        uint? accountId
    )
    {
        Write(
            "Account ID: " +
            (
                accountId.HasValue
                    ? accountId.Value
                        .ToString()
                    : "—"
            )
        );
    }

    public void AppendLog(
        string message
    )
    {
        Write(
            message
        );
    }

    private void Write(
        string message
    )
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(
                "[" +
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff"
                ) +
                "] " +
                message
            );
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed =
                true;

            _writer.Dispose();
        }
    }
}