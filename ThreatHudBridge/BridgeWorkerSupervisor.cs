using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

internal sealed class BridgeWorkerSupervisor :
    IDisposable
{
    private const string DeadlockProcessName =
        "deadlock";

    private const string HealthAddress =
        "http://127.0.0.1:28741/health";

    private const string LaneAdvisorDiagnosticsAddress =
        "http://127.0.0.1:28741/lane-advisor-diagnostics";

    private const string MatchPlayerDetailsAddress =
        "http://127.0.0.1:28741/match-player-details";

    private const long MatchPlayerDetailsPollIntervalMs =
        500;

    private const int WorkerMonitorPollIntervalMs =
        500;

    private const int DeadlockDetectionPollIntervalMs =
        1_000;

    private static readonly JsonSerializerOptions
        JsonOptions =
            new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive =
                    true
            };

    private readonly string[] _args;
    private readonly MainForm _view;

    private readonly object _workerGate =
        new();

    private readonly CancellationTokenSource
        _stopCancellation =
            new();

    private readonly HttpClient _httpClient =
        new()
        {
            Timeout =
                TimeSpan.FromMilliseconds(
                    700
                )
        };

    private Process? _currentWorker;

    private EventWaitHandle?
        _currentStopEvent;

    private int _runStarted;
    private int _stopRequested;

    private long _lastLaneAdvisorEventId;

    private string _lastMatchPlayerDetailsSignature =
        String.Empty;

    private BridgeServiceStatusSnapshot?
        _lastServiceStatuses;

    private bool? _lastHasCurrentMatch;

    private long _nextMatchPlayerDetailsPollAt;

    private bool _disposed;

    public BridgeWorkerSupervisor(
        string[] args,
        MainForm view
    )
    {
        _args =
            args ?? Array.Empty<string>();

        _view =
            view ??
            throw new ArgumentNullException(
                nameof(view)
            );
    }

    public void RequestStop()
    {
        if (
            Interlocked.Exchange(
                ref _stopRequested,
                1
            ) != 0
        )
        {
            return;
        }

        _view.SetRuntimeState(
            BridgeRuntimeState.Stopping,
            "Shutting down Threat HUD Bridge"
        );

        try
        {
            _stopCancellation.Cancel();
        }
        catch (
            ObjectDisposedException
        )
        {
            // Supervisor has already been disposed.
        }

        lock (_workerGate)
        {
            try
            {
                _currentStopEvent?.Set();
            }
            catch (
                ObjectDisposedException
            )
            {
                // Worker has already exited.
            }
        }
    }

    public async Task RunAsync()
    {
        if (
            Interlocked.Exchange(
                ref _runStarted,
                1
            ) != 0
        )
        {
            throw new InvalidOperationException(
                "BridgeWorkerSupervisor is already running."
            );
        }

        var cancellationToken =
            _stopCancellation.Token;

        try
        {
            _view.AppendLog(
                "Desktop supervisor started."
            );

            _view.AppendLog(
                "Worker log: " +
                BridgeWorkerView.LogFilePath
            );

            while (
                !cancellationToken
                    .IsCancellationRequested
            )
            {
                SetWaitingState(
                    "Waiting for Deadlock to start"
                );

                await WaitForDeadlockToStartAsync(
                    cancellationToken
                );

                cancellationToken
                    .ThrowIfCancellationRequested();

                var stopEventName =
                    "Local\\ThreatHudBridge.WorkerStop." +
                    Environment.ProcessId +
                    "." +
                    Guid.NewGuid()
                        .ToString("N");

                using var stopEvent =
                    new EventWaitHandle(
                        false,
                        EventResetMode.ManualReset,
                        stopEventName
                    );

                _lastLaneAdvisorEventId =
                    0;

                _lastMatchPlayerDetailsSignature =
                    String.Empty;

                _nextMatchPlayerDetailsPollAt =
                    0;

                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Waiting
                );

                using var worker =
                    StartWorker(
                        stopEventName
                    );

                lock (_workerGate)
                {
                    _currentWorker =
                        worker;

                    _currentStopEvent =
                        stopEvent;
                }

                _view.AppendLog(
                    "Worker started" +
                    " | PID=" +
                    worker.Id
                );

                var monitorTask =
                    MonitorWorkerAsync(
                        worker,
                        stopEvent,
                        cancellationToken
                    );

                try
                {
                    await WaitForWorkerAsync(
                        worker,
                        stopEvent,
                        cancellationToken
                    );
                }
                finally
                {
                    try
                    {
                        await monitorTask;
                    }
                    catch (
                        OperationCanceledException
                    )
                    when (
                        cancellationToken
                            .IsCancellationRequested
                    )
                    {
                        // Normal shutdown.
                    }

                    lock (_workerGate)
                    {
                        if (
                            ReferenceEquals(
                                _currentWorker,
                                worker
                            )
                        )
                        {
                            _currentWorker =
                                null;

                            _currentStopEvent =
                                null;
                        }
                    }
                }

                if (
                    cancellationToken
                        .IsCancellationRequested
                )
                {
                    break;
                }

                var exitCode =
                    worker.ExitCode;

                _view.AppendLog(
                    "Worker exited" +
                    " | PID=" +
                    worker.Id +
                    " | exitCode=" +
                    exitCode
                );

                if (exitCode == 2)
                {
                    _view.SetRuntimeState(
                        BridgeRuntimeState.Error,

                        "Worker exited " +
                        "with an error"
                    );

                    _view.AppendLog(
                        "Details: " +
                        BridgeWorkerView.LogFilePath
                    );

                    await WaitForDeadlockToCloseAsync(
                        cancellationToken
                    );
                }

                SetWaitingState(
                    "Deadlock closed. " +
                    "Waiting for the next launch"
                );

                await Task.Delay(
                    1000,
                    cancellationToken
                );
            }
        }
        catch (
            OperationCanceledException
        )
        when (
            cancellationToken
                .IsCancellationRequested
        )
        {
            // Normal shutdown.
        }
        catch (Exception error)
        {
            _view.SetRuntimeState(
                BridgeRuntimeState.Error,
                error.Message
            );

            _view.AppendLog(
                "Desktop supervisor error:" +
                Environment.NewLine +
                error
            );
        }
        finally
        {
            SetInactiveState();

            _view.SetRuntimeState(
                BridgeRuntimeState.Stopped,
                "Threat HUD Bridge stopped"
            );
        }
    }

    private Process StartWorker(
        string stopEventName
    )
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName =
                    Application.ExecutablePath,

                WorkingDirectory =
                    AppContext.BaseDirectory,

                UseShellExecute =
                    false,

                CreateNoWindow =
                    true
            };

        startInfo.ArgumentList.Add(
            "--worker"
        );

        startInfo.ArgumentList.Add(
            "--stop-event"
        );

        startInfo.ArgumentList.Add(
            stopEventName
        );

        foreach (
            var argument in _args
        )
        {
            startInfo.ArgumentList.Add(
                argument
            );
        }

        return Process.Start(
            startInfo
        ) ??
        throw new InvalidOperationException(
            "Failed to start the worker process."
        );
    }

    private async Task WaitForWorkerAsync(
        Process worker,
        EventWaitHandle stopEvent,
        CancellationToken cancellationToken
    )
    {
        var exitTask =
            worker.WaitForExitAsync();

        using var waitCancellation =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken
                );

        var cancellationTask =
            Task.Delay(
                Timeout.InfiniteTimeSpan,
                waitCancellation.Token
            );

        var completedTask =
            await Task.WhenAny(
                exitTask,
                cancellationTask
            );

        if (
            completedTask ==
            exitTask
        )
        {
            waitCancellation.Cancel();

            try
            {
                await cancellationTask;
            }
            catch (OperationCanceledException)
            {
            }

            await exitTask;

            return;
        }

        try
        {
            stopEvent.Set();
        }
        catch (
            ObjectDisposedException
        )
        {
            // Worker has already exited.
        }

        var gracefulTask =
            await Task.WhenAny(
                exitTask,

                Task.Delay(
                    TimeSpan.FromSeconds(10)
                )
            );

        if (
            gracefulTask !=
                exitTask &&
            !worker.HasExited
        )
        {
            _view.AppendLog(
                "Worker did not exit within 10 seconds. " +
                "Forcing termination."
            );

            worker.Kill(
                entireProcessTree:
                    true
            );
        }

        await exitTask;
    }

    private async Task MonitorWorkerAsync(
        Process worker,
        EventWaitHandle stopEvent,
        CancellationToken cancellationToken
    )
    {
        var waitingStateApplied =
            false;

        while (
            !worker.HasExited &&
            !cancellationToken
                .IsCancellationRequested
        )
        {
            var deadlockRunning =
                IsProcessRunning(
                    DeadlockProcessName
                );

            if (!deadlockRunning)
            {
                if (!waitingStateApplied)
                {
                    waitingStateApplied =
                        true;

                    SetWaitingState(
                        "Waiting for Deadlock to start"
                    );

                    try
                    {
                        stopEvent.Set();
                    }
                    catch (
                        ObjectDisposedException
                    )
                    {
                    }
                }
            }
            else
            {
                waitingStateApplied =
                    false;

                _view.SetDeadlockRunning(
                    true
                );

                var health =
                    await TryReadHealthAsync(
                        cancellationToken
                    );

                if (health is null)
                {
                    ApplyServiceStatuses(
                        null
                    );

                    _view.SetSteamInitialized(
                        false
                    );

                    _view.SetHttpServerRunning(
                        false
                    );

                    _view.SetAccountId(
                        null
                    );

                    _view.SetRuntimeState(
                        BridgeRuntimeState
                            .StartingSteam,

                        "Initializing Steam API " +
                        "and HTTP server"
                    );
                }
                else
                {
                    ApplyHasCurrentMatch(
                        health.HasCurrentMatch
                    );

                    ApplyServiceStatuses(
                        health.HasCurrentMatch
                            ? health.ServiceStatuses
                            : null
                    );

                    _view.SetSteamInitialized(
                        true
                    );

                    _view.SetHttpServerRunning(
                        true
                    );

                    _view.SetAccountId(
                        health.AccountId
                    );

                    _view.SetRuntimeState(
                        BridgeRuntimeState.Running,
                        "Bridge is ready"
                    );

                    await AppendLaneAdvisorEventsAsync(
                        cancellationToken
                    );

                    await RefreshMatchPlayerDetailsAsync(
                        cancellationToken
                    );
                }
            }

            await Task.Delay(
                WorkerMonitorPollIntervalMs,
                cancellationToken
            );
        }
    }

    private async Task AppendLaneAdvisorEventsAsync(
        CancellationToken cancellationToken
    )
    {
        var address =
            LaneAdvisorDiagnosticsAddress +
            "?after=" +
            _lastLaneAdvisorEventId;

        using var document =
            await TryGetJsonDocumentAsync(
                address,
                cancellationToken
            );

        if (
            document is null ||
            !document.RootElement
                .TryGetProperty(
                    "events",
                    out var eventsElement
                ) ||
            eventsElement.ValueKind !=
                JsonValueKind.Array
        )
        {
            return;
        }

        foreach (
            var eventElement in
            eventsElement.EnumerateArray()
        )
        {
            if (
                !eventElement.TryGetProperty(
                    "id",
                    out var idElement
                ) ||
                !idElement.TryGetInt64(
                    out var eventId
                ) ||
                eventId <=
                    _lastLaneAdvisorEventId
            )
            {
                continue;
            }

            _lastLaneAdvisorEventId =
                eventId;

            if (
                eventElement.TryGetProperty(
                    "message",
                    out var messageElement
                )
            )
            {
                var message =
                    messageElement.GetString();

                if (
                    !String.IsNullOrWhiteSpace(
                        message
                    )
                )
                {
                    _view.AppendLog(
                        message
                    );
                }
            }
        }
    }

    private async Task RefreshMatchPlayerDetailsAsync(
        CancellationToken cancellationToken
    )
    {
        var now =
            Environment.TickCount64;

        if (
            now <
                _nextMatchPlayerDetailsPollAt
        )
        {
            return;
        }

        _nextMatchPlayerDetailsPollAt =
            now +
            MatchPlayerDetailsPollIntervalMs;

        using var document =
            await TryGetJsonDocumentAsync(
                MatchPlayerDetailsAddress,
                cancellationToken
            );

        if (document is null)
        {
            return;
        }

        var root =
            document.RootElement;

        var signature =
            root.GetRawText();

        if (
            String.Equals(
                signature,
                _lastMatchPlayerDetailsSignature,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        _lastMatchPlayerDetailsSignature =
            signature;

        DeadlockMatchPlayerDetailsSnapshot?
            snapshot;

        try
        {
            snapshot =
                JsonSerializer.Deserialize<
                    DeadlockMatchPlayerDetailsSnapshot
                >(
                    signature,
                    JsonOptions
                );
        }
        catch (JsonException)
        {
            _view.SetMatchPlayerDetails(
                MatchPlayerDetailsUiState.Error,
                detail:
                    "Invalid local match-player-details response."
            );

            return;
        }

        if (snapshot is null)
        {
            _view.SetMatchPlayerDetails(
                MatchPlayerDetailsUiState.Error,
                detail:
                    "Empty local match-player-details response."
            );

            return;
        }

        switch (snapshot.Status)
        {
            case "waiting":
                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Waiting
                );

                return;

            case "loading":
                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Loading
                );

                return;

            case "failed":
                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Error,
                    detail:
                        snapshot.Error
                );

                return;

            case "ready":
                if (
                    snapshot.Players.Count !=
                        12
                )
                {
                    _view.SetMatchPlayerDetails(
                        MatchPlayerDetailsUiState.Error,
                        detail:
                            "Expected 12 current match players."
                    );

                    return;
                }

                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Ready,
                    snapshot.Players
                );

                return;

            default:
                _view.SetMatchPlayerDetails(
                    MatchPlayerDetailsUiState.Error,
                    detail:
                        "Unknown match-player-details status."
                );

                return;
        }
    }

    private async Task<HealthSnapshot?>
        TryReadHealthAsync(
            CancellationToken cancellationToken
        )
    {
        using var document =
            await TryGetJsonDocumentAsync(
                HealthAddress,
                cancellationToken
            );

        if (
            document is null ||
            !document.RootElement
                .TryGetProperty(
                    "ownAccountId",
                    out var accountIdElement
                ) ||
            !accountIdElement
                .TryGetUInt32(
                    out var accountId
                ) ||
            !TryReadHasCurrentMatch(
                document.RootElement,
                out var hasCurrentMatch
            )
        )
        {
            return null;
        }

        return new HealthSnapshot(
            accountId,
            hasCurrentMatch,
            ReadServiceStatuses(
                document.RootElement
            )
        );
    }

    private static bool TryReadHasCurrentMatch(
        JsonElement root,
        out bool hasCurrentMatch
    )
    {
        hasCurrentMatch =
            false;

        if (
            !root.TryGetProperty(
                "hasCurrentMatch",
                out var value
            )
        )
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                hasCurrentMatch =
                    true;

                return true;

            case JsonValueKind.False:
                return true;

            default:
                return false;
        }
    }

    private static BridgeServiceStatusSnapshot
        ReadServiceStatuses(
            JsonElement root
        )
    {
        if (
            !root.TryGetProperty(
                "services",
                out var services
            ) ||
            services.ValueKind !=
                JsonValueKind.Object
        )
        {
            return BridgeServiceStatusSnapshot
                .InProgress;
        }

        return new BridgeServiceStatusSnapshot(
            Winrate:
                ReadServiceState(
                    services,
                    "winrate"
                ),

            Rank:
                ReadServiceState(
                    services,
                    "rank"
                ),

            Adviser:
                ReadServiceState(
                    services,
                    "adviser"
                ),

            HeroDamage:
                ReadServiceState(
                    services,
                    "heroDamage"
                )
        );
    }

    private static BridgeServiceState ReadServiceState(
        JsonElement services,
        string propertyName
    )
    {
        if (
            services.TryGetProperty(
                propertyName,
                out var value
            ) &&
            value.ValueKind ==
                JsonValueKind.String &&
            BridgeServiceStateText
                .TryParseWireValue(
                    value.GetString(),
                    out var state
                )
        )
        {
            return state;
        }

        return BridgeServiceState
            .InProgress;
    }

    /*
     * Single implementation for reading JSON
     * from the local worker HTTP API.
     *
     * Health and Lane Advisor differ only in
     * payload parsing, so the networking code
     * is not duplicated.
     */
    private async Task<JsonDocument?>
        TryGetJsonDocumentAsync(
            string address,
            CancellationToken cancellationToken
        )
    {
        try
        {
            using var response =
                await _httpClient.GetAsync(
                    address,
                    cancellationToken
                );

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream =
                await response.Content
                    .ReadAsStreamAsync(
                        cancellationToken
                    );

            return await JsonDocument
                .ParseAsync(
                    stream,
                    cancellationToken:
                        cancellationToken
                );
        }
        catch (
            OperationCanceledException
        )
        when (
            cancellationToken
                .IsCancellationRequested
        )
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task
        WaitForDeadlockToStartAsync(
            CancellationToken cancellationToken
        )
    {
        while (
            !IsProcessRunning(
                DeadlockProcessName
            )
        )
        {
            await Task.Delay(
                DeadlockDetectionPollIntervalMs,
                cancellationToken
            );
        }
    }

    private static async Task
        WaitForDeadlockToCloseAsync(
            CancellationToken cancellationToken
        )
    {
        while (
            IsProcessRunning(
                DeadlockProcessName
            )
        )
        {
            await Task.Delay(
                500,
                cancellationToken
            );
        }
    }

    private void SetWaitingState(
        string detail
    )
    {
        SetInactiveState();

        _view.SetRuntimeState(
            BridgeRuntimeState
                .WaitingForDeadlock,
            detail
        );
    }

    private void SetInactiveState()
    {
        ApplyHasCurrentMatch(
            false
        );

        ApplyServiceStatuses(
            null
        );

        _view.SetDeadlockRunning(
            false
        );

        _view.SetSteamInitialized(
            false
        );

        _view.SetHttpServerRunning(
            false
        );

        _view.SetAccountId(
            null
        );

        _lastMatchPlayerDetailsSignature =
            String.Empty;

        _nextMatchPlayerDetailsPollAt =
            0;

        _view.SetMatchPlayerDetails(
            MatchPlayerDetailsUiState.Waiting
        );
    }

    private void ApplyServiceStatuses(
        BridgeServiceStatusSnapshot? snapshot
    )
    {
        if (
            Equals(
                _lastServiceStatuses,
                snapshot
            )
        )
        {
            return;
        }

        _lastServiceStatuses =
            snapshot;

        _view.SetServiceStates(
            snapshot
        );
    }

    private void ApplyHasCurrentMatch(
        bool hasCurrentMatch
    )
    {
        if (
            _lastHasCurrentMatch ==
                hasCurrentMatch
        )
        {
            return;
        }

        _lastHasCurrentMatch =
            hasCurrentMatch;

        _view.SetHasCurrentMatch(
            hasCurrentMatch
        );

        if (!hasCurrentMatch)
        {
            /*
             * Clear the previous roster immediately. The details endpoint may
             * be briefly unavailable exactly when a match ends, and hidden
             * stale rows must not flash when the next match begins.
             */
            _view.SetMatchPlayerDetails(
                MatchPlayerDetailsUiState.Waiting
            );
        }
    }

    private static bool IsProcessRunning(
        string processName
    )
    {
        var processes =
            Process.GetProcessesByName(
                processName
            );

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (
                var process in processes
            )
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed =
            true;

        RequestStop();

        _httpClient.Dispose();
        _stopCancellation.Dispose();
    }

    private sealed class HealthSnapshot
    {
        public HealthSnapshot(
            uint accountId,
            bool hasCurrentMatch,
            BridgeServiceStatusSnapshot
                serviceStatuses
        )
        {
            AccountId =
                accountId;

            HasCurrentMatch =
                hasCurrentMatch;

            ServiceStatuses =
                serviceStatuses ??
                throw new ArgumentNullException(
                    nameof(serviceStatuses)
                );
        }

        public uint AccountId
        {
            get;
        }

        public bool HasCurrentMatch
        {
            get;
        }

        public BridgeServiceStatusSnapshot
            ServiceStatuses
        {
            get;
        }
    }
}
