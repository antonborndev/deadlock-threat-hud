using System.Collections.Concurrent;
using System.Text.Json;

internal sealed class MatchHistoryCaptureService
{
    private static readonly TimeSpan SnapshotInterval =
        TimeSpan.FromSeconds(
            10
        );

    private static readonly JsonSerializerOptions
        SnapshotJsonOptions =
            new()
            {
                PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase
            };

    private readonly object _currentMatchLifecycleGate;
    private readonly CurrentMatchContextService _contextService;
    private readonly CurrentMatchPlayerDetailsCoordinator
        _playerDetailsService;
    private readonly BridgeServiceStatusStore _serviceStatusStore;
    private readonly DeadlockLaneAdvisorService _laneAdvisorService;
    private readonly uint? _localPlayerAccountId;
    private readonly MatchHistoryStore _historyStore;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _captureGate =
        new(
            1,
            1
        );

    private readonly SemaphoreSlim _captureRequests =
        new(
            0,
            1
        );

    private readonly ConcurrentQueue<(
        ulong MatchId,
        DateTimeOffset CapturedAtUtc,
        string SnapshotJson
    )> _pendingCaptures =
        new();

    public MatchHistoryCaptureService(
        object currentMatchLifecycleGate,
        CurrentMatchContextService contextService,
        CurrentMatchPlayerDetailsCoordinator playerDetailsService,
        BridgeServiceStatusStore serviceStatusStore,
        DeadlockLaneAdvisorService laneAdvisorService,
        uint ownAccountId,
        MatchHistoryStore historyStore,
        Action<string>? log = null
    )
    {
        _currentMatchLifecycleGate =
            currentMatchLifecycleGate ??
            throw new ArgumentNullException(
                nameof(currentMatchLifecycleGate)
            );

        _contextService =
            contextService ??
            throw new ArgumentNullException(
                nameof(contextService)
            );

        _playerDetailsService =
            playerDetailsService ??
            throw new ArgumentNullException(
                nameof(playerDetailsService)
            );

        _serviceStatusStore =
            serviceStatusStore ??
            throw new ArgumentNullException(
                nameof(serviceStatusStore)
            );

        _laneAdvisorService =
            laneAdvisorService ??
            throw new ArgumentNullException(
                nameof(laneAdvisorService)
            );

        _localPlayerAccountId =
            ownAccountId == 0
                ? null
                : ownAccountId;

        _historyStore =
            historyStore ??
            throw new ArgumentNullException(
                nameof(historyStore)
            );

        _log =
            log ??
            (_ => { });
    }

    public async Task RunAsync(
        CancellationToken cancellationToken
    )
    {
        using var timer =
            new PeriodicTimer(
                SnapshotInterval
            );

        try
        {
            await CaptureNowAsync(
                cancellationToken
            );

            var timerWait =
                timer.WaitForNextTickAsync(
                    cancellationToken
                )
                .AsTask();

            var requestWait =
                _captureRequests.WaitAsync(
                    cancellationToken
                );

            while (true)
            {
                var completed =
                    await Task.WhenAny(
                        timerWait,
                        requestWait
                    );

                if (
                    ReferenceEquals(
                        completed,
                        timerWait
                    )
                )
                {
                    if (!await timerWait)
                    {
                        break;
                    }

                    timerWait =
                        timer.WaitForNextTickAsync(
                            cancellationToken
                        )
                        .AsTask();
                }
                else
                {
                    await requestWait;

                    requestWait =
                        _captureRequests.WaitAsync(
                            cancellationToken
                        );
                }

                await CaptureNowAsync(
                    cancellationToken
                );
            }
        }
        catch (
            OperationCanceledException
        ) when (
            cancellationToken.IsCancellationRequested
        )
        {
        }
    }

    public void RequestCapture()
    {
        try
        {
            _captureRequests.Release();
        }
        catch (SemaphoreFullException)
        {
            // One queued capture is sufficient for the newest match state.
        }
    }

    public void QueueCurrentSnapshotWhileLocked()
    {
        if (
            !Monitor.IsEntered(
                _currentMatchLifecycleGate
            )
        )
        {
            throw new InvalidOperationException(
                "The current-match lifecycle gate must be held " +
                "while queueing a transition snapshot."
            );
        }

        try
        {
            var capture =
                BuildCaptureWhileLocked();

            if (!capture.HasValue)
            {
                return;
            }

            _pendingCaptures.Enqueue(
                capture.Value
            );

            RequestCapture();
        }
        catch (Exception error)
        {
            _log(
                "Match history transition snapshot ERROR: " +
                error.Message
            );
        }
    }

    public async Task CaptureNowAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _captureGate.WaitAsync(
                cancellationToken
            );

            try
            {
                while (
                    _pendingCaptures.TryPeek(
                        out var pendingCapture
                    )
                )
                {
                    await PersistCaptureAsync(
                        pendingCapture,
                        cancellationToken
                    );

                    _pendingCaptures.TryDequeue(
                        out _
                    );
                }

                var capture =
                    BuildCapture();

                if (!capture.HasValue)
                {
                    return;
                }

                await PersistCaptureAsync(
                    capture.Value,
                    cancellationToken
                );
            }
            finally
            {
                _captureGate.Release();
            }
        }
        catch (
            OperationCanceledException
        ) when (
            cancellationToken.IsCancellationRequested
        )
        {
            throw;
        }
        catch (Exception error)
        {
            _log(
                "Match history snapshot ERROR: " +
                error.Message
            );
        }
    }

    private Task PersistCaptureAsync(
        (
            ulong MatchId,
            DateTimeOffset CapturedAtUtc,
            string SnapshotJson
        ) capture,
        CancellationToken cancellationToken
    )
    {
        return _historyStore.UpsertSnapshotAsync(
            capture.MatchId,
            capture.CapturedAtUtc,
            capture.SnapshotJson,
            cancellationToken
        );
    }

    private (
        ulong MatchId,
        DateTimeOffset CapturedAtUtc,
        string SnapshotJson
    )? BuildCapture()
    {
        lock (_currentMatchLifecycleGate)
        {
            return BuildCaptureWhileLocked();
        }
    }

    private (
        ulong MatchId,
        DateTimeOffset CapturedAtUtc,
        string SnapshotJson
    )? BuildCaptureWhileLocked()
    {
        var contextSnapshot =
            _contextService.GetSnapshot();

        var matchId =
            contextSnapshot.MatchId;

        if (
            !MatchHistoryStore.IsPlausibleMatchId(
                matchId
            )
        )
        {
            return null;
        }

        var capturedAtUtc =
            DateTimeOffset.UtcNow;

        var playerDetails =
            _playerDetailsService
                .GetSnapshotForMatch(
                    matchId
                );

        var hasResolvedPlayerWithHero =
            playerDetails.Players.Any(
                static player =>
                    player.AccountId != 0 &&
                    player.HeroId != 0
            );

        if (!hasResolvedPlayerWithHero)
        {
            return null;
        }

        var serviceStatuses =
            _serviceStatusStore
                .GetSnapshot();

        var moduleSettings =
            BridgeModuleSettingsPersistence
                .Load();

        var currentLaneStats =
            moduleSettings.IsEnabled(
                BridgeServiceKind.Adviser
            ) &&
            serviceStatuses.Adviser ==
                BridgeServiceState.Completed
                ? _laneAdvisorService
                    .GetCurrentLaneStatsSnapshot()
                : null;

        var liveDamageSnapshot =
            contextSnapshot.LiveDamage;

        var heroDamage =
            liveDamageSnapshot.MatchId ==
                matchId
                ? new
                {
                    liveDamageSnapshot.MatchId,
                    liveDamageSnapshot.Status,
                    liveDamageSnapshot.HeroStatsReadyAtUtc,
                    liveDamageSnapshot.ScheduledStartAtUtc,
                    liveDamageSnapshot.BroadcastReadyAtUtc,
                    liveDamageSnapshot.StartedAtUtc,
                    liveDamageSnapshot.ConnectedAtUtc,
                    liveDamageSnapshot.LastEventAtUtc,
                    liveDamageSnapshot.LastSampleAtUtc,
                    liveDamageSnapshot.StatusMessage,
                    liveDamageSnapshot.Error,
                    liveDamageSnapshot.Source,
                    liveDamageSnapshot.BroadcastProtocol,
                    liveDamageSnapshot.BroadcastTickRate,
                    liveDamageSnapshot.InitialFragment,
                    liveDamageSnapshot.BroadcastStepCount,
                    liveDamageSnapshot.PlayerSampleCount,
                    liveDamageSnapshot.LastTick,
                    liveDamageSnapshot.Players
                }
                : null;

        var snapshotJson =
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion =
                        1,

                    matchId,
                    capturedAtUtc,

                    localPlayerWon =
                        contextSnapshot
                            .LocalPlayerWon,

                    localPlayerAccountId =
                        _localPlayerAccountId,

                    context =
                        new
                        {
                            contextSnapshot
                                .HeroDamageAllowedForMatch,

                            contextSnapshot
                                .MatchObservedAtUtc,

                            contextSnapshot
                                .HeroStatsGeneratedAtUtc,

                            contextSnapshot
                                .HeroStatsReadyAtUtc
                        },

                    modules =
                        new
                        {
                            winrate =
                                moduleSettings.IsEnabled(
                                    BridgeServiceKind.Winrate
                                ),

                            rank =
                                moduleSettings.IsEnabled(
                                    BridgeServiceKind.Rank
                                ),

                            adviser =
                                moduleSettings.IsEnabled(
                                    BridgeServiceKind.Adviser
                                ),

                            heroDamage =
                                moduleSettings.IsEnabled(
                                    BridgeServiceKind.HeroDamage
                                )
                        },

                    services =
                        new
                        {
                            winrate =
                                BridgeServiceStateText
                                    .ToWireValue(
                                        serviceStatuses
                                            .Winrate
                                    ),

                            rank =
                                BridgeServiceStateText
                                    .ToWireValue(
                                        serviceStatuses
                                            .Rank
                                    ),

                            adviser =
                                BridgeServiceStateText
                                    .ToWireValue(
                                        serviceStatuses
                                            .Adviser
                                    ),

                            heroDamage =
                                BridgeServiceStateText
                                    .ToWireValue(
                                        serviceStatuses
                                            .HeroDamage
                                    )
                        },

                    playerDetails,
                    heroDamage,
                    laneStats =
                        currentLaneStats
                },
                SnapshotJsonOptions
            );

        return (
            matchId,
            capturedAtUtc,
            snapshotJson
        );
    }
}
