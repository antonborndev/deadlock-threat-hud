using System.Globalization;
using System.Text.Json;

internal sealed class CurrentMatchBroadcastProbeService : IAsyncDisposable
{
    private const string DeadlockApiBaseUrl =
        "https://api.deadlock-api.com/v1/matches";

    private const int DeadlockApiMaximumRequestCount = 3;

    private static readonly TimeSpan DeadlockApiRequestTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan DeadlockApiRetryDelay =
        TimeSpan.FromSeconds(5);

    private static readonly HttpClient DeadlockApiHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly Action<string> _log;
    private readonly Action<CurrentMatchBroadcastReady>? _readyHandler;
    private readonly object _stateGate = new();

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private long _generation;

    private CurrentMatchBroadcastProbeSnapshot _snapshot =
        CurrentMatchBroadcastProbeSnapshot.Waiting;

    public CurrentMatchBroadcastProbeService(
        Action<string>? log = null,
        Action<CurrentMatchBroadcastReady>? readyHandler = null)
    {
        _log = log ?? (_ => { });
        _readyHandler = readyHandler;
    }

    public void ResetMatch(ulong matchId)
    {
        CancellationTokenSource? previousCancellation;
        Task? previousTask;

        lock (_stateGate)
        {
            if (_snapshot.MatchId == matchId)
            {
                return;
            }

            previousCancellation = _runCancellation;
            previousTask = _runTask;
            _runCancellation = null;
            _runTask = null;
            _generation++;

            _snapshot = matchId == 0
                ? CurrentMatchBroadcastProbeSnapshot.Waiting
                : CurrentMatchBroadcastProbeSnapshot.ForMatch(matchId);
        }

        Cancel(previousCancellation);
        _ = DisposeCompletedRunAsync(previousTask, previousCancellation);
    }

    public bool MarkScheduled(
        ulong matchId,
        DateTimeOffset heroStatsReadyAtUtc,
        DateTimeOffset scheduledStartAtUtc)
    {
        lock (_stateGate)
        {
            if (
                matchId == 0 ||
                _snapshot.MatchId != matchId ||
                _runTask is not null)
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                Status = "scheduled",
                HeroStatsReadyAtUtc = heroStatsReadyAtUtc,
                ScheduledStartAtUtc = scheduledStartAtUtc,
                StatusMessage = "Waiting for 5-second post-stats delay",
                Error = null
            };

            return true;
        }
    }

    public bool StartForMatch(ulong matchId)
    {
        lock (_stateGate)
        {
            /*
             * Keep the completed task assigned until ResetMatch. This makes
             * /live/url a one-shot workflow for the current match epoch.
             */
            if (
                matchId == 0 ||
                _snapshot.MatchId != matchId ||
                _runTask is not null)
            {
                return false;
            }

            var generation = _generation;
            var cancellation = new CancellationTokenSource();
            var now = DateTimeOffset.UtcNow;

            _runCancellation = cancellation;
            _snapshot = _snapshot with
            {
                Status = "resolving-broadcast-url",
                StartedAtUtc = now,
                LastEventAtUtc = now,
                StatusMessage = "Requesting broadcast URL from Deadlock API",
                Error = null,
                BroadcastSource = "deadlock-api-live-url"
            };

            _runTask = RunAsync(
                matchId,
                generation,
                cancellation.Token);

            return true;
        }
    }

    public CurrentMatchBroadcastProbeSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            return _snapshot;
        }
    }

    private async Task RunAsync(
        ulong matchId,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestedAtUtc = DateTimeOffset.UtcNow;

            _log(
                "Current match broadcast probe: Deadlock API /live/url resolution starting" +
                " | matchId=" + matchId +
                " | requestedAtUtc=" + requestedAtUtc.ToString("O") +
                " | maxRequests=" + DeadlockApiMaximumRequestCount +
                " | retryDelay=" + FormatSeconds(DeadlockApiRetryDelay));

            var resolved = await ResolveBroadcastUrlAsync(
                matchId,
                generation,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (!MarkReady(matchId, generation, resolved))
            {
                return;
            }

            var apiDuration = resolved.ResolvedAtUtc - requestedAtUtc;

            _log(
                "Current match broadcast probe: broadcast URL READY" +
                " | matchId=" + matchId +
                " | resolvedAtUtc=" + resolved.ResolvedAtUtc.ToString("O") +
                " | apiDuration=" + FormatSeconds(apiDuration) +
                " | apiRequests=" + resolved.RequestNumber +
                " | lobbyId=" +
                (resolved.LobbyId?.ToString(CultureInfo.InvariantCulture) ?? "—") +
                " | relayProbeRequests=0" +
                " | relayOwner=dynamic-haste-sidecar" +
                " | url=" + resolved.BroadcastUrl);

            NotifyReady(
                new CurrentMatchBroadcastReady(
                    MatchId: matchId,
                    BroadcastUrl: resolved.BroadcastUrl,
                    LobbyId: resolved.LobbyId,
                    ResolvedAtUtc: resolved.ResolvedAtUtc,
                    ReadyAtUtc: resolved.ResolvedAtUtc));
        }
        catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            MarkError(matchId, generation, error.Message);

            _log(
                "Current match broadcast probe ERROR" +
                " | matchId=" + matchId +
                " | " + error.Message);
        }
    }

    private async Task<BroadcastUrlResolution> ResolveBroadcastUrlAsync(
        ulong matchId,
        long generation,
        CancellationToken cancellationToken)
    {
        Exception? lastTransientError = null;

        for (
            var requestNumber = 1;
            requestNumber <= DeadlockApiMaximumRequestCount;
            requestNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsCurrentRunThreadSafe(matchId, generation))
            {
                throw new OperationCanceledException(
                    "Current match changed while resolving broadcast URL.",
                    cancellationToken);
            }

            var attemptStartedAtUtc = DateTimeOffset.UtcNow;
            MarkDeadlockApiAttemptStarted(
                matchId,
                generation,
                requestNumber);

            _log(
                "Current match broadcast probe: Deadlock API /live/url starting" +
                " | matchId=" + matchId +
                " | requestedAtUtc=" + attemptStartedAtUtc.ToString("O") +
                " | requestNumber=" + requestNumber +
                " | maxRequests=" + DeadlockApiMaximumRequestCount);

            try
            {
                return await ResolveBroadcastUrlOnceAsync(
                    matchId,
                    requestNumber,
                    cancellationToken);
            }
            catch (Exception error)
            when (IsRetryableDeadlockApiException(error))
            {
                lastTransientError = error;

                if (requestNumber >= DeadlockApiMaximumRequestCount)
                {
                    break;
                }

                MarkDeadlockApiRetryScheduled(
                    matchId,
                    generation,
                    requestNumber,
                    error.Message);

                _log(
                    "Current match broadcast probe: Deadlock API /live/url retry scheduled" +
                    " | matchId=" + matchId +
                    " | failedRequestNumber=" + requestNumber +
                    " | nextRequestNumber=" + (requestNumber + 1) +
                    " | delay=" + FormatSeconds(DeadlockApiRetryDelay) +
                    " | reason=" + error.Message);

                await Task.Delay(
                    DeadlockApiRetryDelay,
                    cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Deadlock API /live/url failed after " +
            DeadlockApiMaximumRequestCount +
            " requests. Last error: " +
            (lastTransientError?.Message ?? "unknown transient error"),
            lastTransientError);
    }

    private static async Task<BroadcastUrlResolution>
        ResolveBroadcastUrlOnceAsync(
            ulong matchId,
            int requestNumber,
            CancellationToken cancellationToken)
    {
        var url =
            DeadlockApiBaseUrl +
            "/" +
            matchId.ToString(CultureInfo.InvariantCulture) +
            "/live/url";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "DeadlockThreatHudBridge/1.0");

        using var response = await SendDeadlockApiRequestAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var message =
                "Deadlock API /live/url returned HTTP " +
                statusCode +
                " " +
                (response.ReasonPhrase ?? String.Empty);

            if (IsTransientDeadlockApiStatus(statusCode))
            {
                throw new DeadlockApiTransientException(message);
            }

            throw new InvalidOperationException(message);
        }

        var json = await ReadDeadlockApiTextAsync(
            response,
            cancellationToken);

        using var document = JsonDocument.Parse(json);

        if (
            !document.RootElement.TryGetProperty(
                "broadcast_url",
                out var broadcastUrlValue) ||
            broadcastUrlValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "Deadlock API /live/url response does not contain broadcast_url.");
        }

        var broadcastUrl = broadcastUrlValue.GetString();

        if (String.IsNullOrWhiteSpace(broadcastUrl))
        {
            throw new InvalidOperationException(
                "Deadlock API /live/url returned an empty broadcast_url.");
        }

        ulong? lobbyId = null;

        if (
            document.RootElement.TryGetProperty(
                "lobby_id",
                out var lobbyIdValue) &&
            lobbyIdValue.ValueKind == JsonValueKind.Number &&
            lobbyIdValue.TryGetUInt64(out var parsedLobbyId))
        {
            lobbyId = parsedLobbyId;
        }

        return new BroadcastUrlResolution(
            BroadcastUrl: broadcastUrl!,
            LobbyId: lobbyId,
            ResolvedAtUtc: DateTimeOffset.UtcNow,
            RequestNumber: requestNumber);
    }

    private static async Task<HttpResponseMessage> SendDeadlockApiRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(DeadlockApiRequestTimeout);

        try
        {
            return await DeadlockApiHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException)
        when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Deadlock API /live/url request timed out after " +
                DeadlockApiRequestTimeout.TotalSeconds.ToString(
                    "0",
                    CultureInfo.InvariantCulture) +
                " seconds.");
        }
    }

    private static async Task<string> ReadDeadlockApiTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(DeadlockApiRequestTimeout);

        try
        {
            return await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Deadlock API /live/url response body timed out after " +
                DeadlockApiRequestTimeout.TotalSeconds.ToString(
                    "0",
                    CultureInfo.InvariantCulture) +
                " seconds.");
        }
    }

    private void MarkDeadlockApiAttemptStarted(
        ulong matchId,
        long generation,
        int requestNumber)
    {
        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Status = "resolving-broadcast-url",
                LastEventAtUtc = DateTimeOffset.UtcNow,
                StatusMessage =
                    "Requesting broadcast URL from Deadlock API (" +
                    requestNumber.ToString(CultureInfo.InvariantCulture) +
                    "/" +
                    DeadlockApiMaximumRequestCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ")",
                Error = null,
                BroadcastSource = "deadlock-api-live-url"
            };
        }
    }

    private void MarkDeadlockApiRetryScheduled(
        ulong matchId,
        long generation,
        int failedRequestNumber,
        string error)
    {
        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Status = "waiting-for-broadcast-url-retry",
                LastEventAtUtc = DateTimeOffset.UtcNow,
                StatusMessage =
                    "Deadlock API request " +
                    failedRequestNumber.ToString(CultureInfo.InvariantCulture) +
                    " failed; retrying in " +
                    DeadlockApiRetryDelay.TotalSeconds.ToString(
                        "0",
                        CultureInfo.InvariantCulture) +
                    " seconds",
                Error = error,
                BroadcastSource = "deadlock-api-live-url"
            };
        }
    }

    private bool MarkReady(
        ulong matchId,
        long generation,
        BroadcastUrlResolution resolved)
    {
        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                Status = "broadcast-url-resolved",
                LastEventAtUtc = resolved.ResolvedAtUtc,
                StatusMessage =
                    "Broadcast URL resolved; sidecar owns Valve relay bootstrap",
                Error = null,
                BroadcastSource = "deadlock-api-live-url",
                BroadcastUrl = resolved.BroadcastUrl,
                LobbyId = resolved.LobbyId,
                RelayProbeAttempt = 0,
                RelaySyncReady = false,
                RelayStartReady = false,
                RelayFullReady = false,
                LastRelayProbeAtUtc = null
            };

            return true;
        }
    }

    private void NotifyReady(CurrentMatchBroadcastReady ready)
    {
        if (_readyHandler is null)
        {
            return;
        }

        try
        {
            _readyHandler(ready);
        }
        catch (Exception error)
        {
            _log(
                "Current match broadcast probe: URL handler ERROR" +
                " | matchId=" + ready.MatchId +
                " | " + error.Message);
        }
    }

    private void MarkError(
        ulong matchId,
        long generation,
        string error)
    {
        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Status = "error",
                LastEventAtUtc = DateTimeOffset.UtcNow,
                StatusMessage = "Broadcast URL resolution failed",
                Error = error
            };
        }
    }

    private bool IsCurrentRun(ulong matchId, long generation) =>
        _generation == generation &&
        _snapshot.MatchId == matchId;

    private bool IsCurrentRunThreadSafe(ulong matchId, long generation)
    {
        lock (_stateGate)
        {
            return IsCurrentRun(matchId, generation);
        }
    }

    private static bool IsRetryableDeadlockApiException(Exception error) =>
        error is DeadlockApiTransientException ||
        error is TimeoutException ||
        error is HttpRequestException;

    private static bool IsTransientDeadlockApiStatus(int statusCode) =>
        statusCode == 429 ||
        statusCode == 502 ||
        statusCode == 503 ||
        statusCode == 504;

    private static string FormatSeconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString(
            "0.000",
            CultureInfo.InvariantCulture) +
        "s";

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task DisposeCompletedRunAsync(
        Task? task,
        CancellationTokenSource? cancellation)
    {
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;

        lock (_stateGate)
        {
            _generation++;
            task = _runTask;
            cancellation = _runCancellation;
            _runTask = null;
            _runCancellation = null;
            _snapshot = CurrentMatchBroadcastProbeSnapshot.Waiting;
        }

        Cancel(cancellation);

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        cancellation?.Dispose();
    }

    private sealed record BroadcastUrlResolution(
        string BroadcastUrl,
        ulong? LobbyId,
        DateTimeOffset ResolvedAtUtc,
        int RequestNumber);

    private sealed class DeadlockApiTransientException : Exception
    {
        public DeadlockApiTransientException(string message)
            : base(message)
        {
        }
    }
}

internal sealed record CurrentMatchBroadcastReady(
    ulong MatchId,
    string BroadcastUrl,
    ulong? LobbyId,
    DateTimeOffset ResolvedAtUtc,
    DateTimeOffset ReadyAtUtc);

internal sealed record CurrentMatchBroadcastProbeSnapshot(
    ulong MatchId,
    string Status,
    DateTimeOffset? HeroStatsReadyAtUtc,
    DateTimeOffset? ScheduledStartAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastEventAtUtc,
    string? StatusMessage,
    string? Error,
    string? BroadcastSource,
    string? BroadcastUrl,
    ulong? LobbyId,
    int RelayProbeAttempt,
    bool RelaySyncReady,
    bool RelayStartReady,
    bool RelayFullReady,
    DateTimeOffset? LastRelayProbeAtUtc)
{
    /*
     * Relay fields remain for diagnostics JSON compatibility. They stay empty
     * because only the Rust sidecar now accesses Valve relay endpoints.
     */
    public static CurrentMatchBroadcastProbeSnapshot Waiting => new(
        MatchId: 0,
        Status: "waiting",
        HeroStatsReadyAtUtc: null,
        ScheduledStartAtUtc: null,
        StartedAtUtc: null,
        LastEventAtUtc: null,
        StatusMessage: "Waiting for current match ID",
        Error: null,
        BroadcastSource: null,
        BroadcastUrl: null,
        LobbyId: null,
        RelayProbeAttempt: 0,
        RelaySyncReady: false,
        RelayStartReady: false,
        RelayFullReady: false,
        LastRelayProbeAtUtc: null);

    public static CurrentMatchBroadcastProbeSnapshot ForMatch(ulong matchId) => new(
        MatchId: matchId,
        Status: "waiting-for-hero-stats",
        HeroStatsReadyAtUtc: null,
        ScheduledStartAtUtc: null,
        StartedAtUtc: null,
        LastEventAtUtc: null,
        StatusMessage: "Waiting for CURRENT HERO STATS",
        Error: null,
        BroadcastSource: null,
        BroadcastUrl: null,
        LobbyId: null,
        RelayProbeAttempt: 0,
        RelaySyncReady: false,
        RelayStartReady: false,
        RelayFullReady: false,
        LastRelayProbeAtUtc: null);
}
