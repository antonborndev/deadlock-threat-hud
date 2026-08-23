internal sealed class CurrentMatchContextService :
    IAsyncDisposable
{
    private static readonly TimeSpan
        BroadcastProbeStartDelay =
            TimeSpan.FromSeconds(5);

    private readonly Action<string> _log;

    private readonly object _stateGate =
        new();

    private readonly CurrentMatchBroadcastProbeService
        _broadcastProbeService;

    private readonly CurrentMatchLiveDamageService
        _liveDamageService;

    private CancellationTokenSource?
        _scheduleCancellation;

    private Task?
        _scheduleTask;

    private ulong _matchId;

    private long _generation;

    private DateTimeOffset?
        _lastReceivedAtUtc;

    private DateTimeOffset?
        _matchObservedAtUtc;

    private DateTimeOffset?
        _heroStatsGeneratedAtUtc;

    private DateTimeOffset?
        _heroStatsReadyAtUtc;

    private DateTimeOffset?
        _broadcastProbeScheduledStartAtUtc;

    public CurrentMatchContextService(
        Action<string>? log = null
    )
    {
        _log =
            log ??
            (_ => { });

        _liveDamageService =
            new CurrentMatchLiveDamageService(
                _log
            );

        _broadcastProbeService =
            new CurrentMatchBroadcastProbeService(
                _log,
                OnBroadcastReady
            );
    }

    public bool Update(
        ulong matchId
    )
    {
        var changed =
            false;

        CancellationTokenSource?
            previousScheduleCancellation =
                null;

        Task?
            previousScheduleTask =
                null;

        lock (_stateGate)
        {
            changed =
                _matchId !=
                    matchId;

            var receivedAtUtc =
                DateTimeOffset.UtcNow;

            _lastReceivedAtUtc =
                receivedAtUtc;

            if (changed)
            {
                _matchId =
                    matchId;

                _matchObservedAtUtc =
                    matchId == 0
                        ? null
                        : receivedAtUtc;

                _generation++;

                _heroStatsGeneratedAtUtc =
                    null;

                _heroStatsReadyAtUtc =
                    null;

                _broadcastProbeScheduledStartAtUtc =
                    null;

                previousScheduleCancellation =
                    _scheduleCancellation;

                previousScheduleTask =
                    _scheduleTask;

                _scheduleCancellation =
                    null;

                _scheduleTask =
                    null;
            }
        }

        if (!changed)
        {
            return false;
        }

        Cancel(
            previousScheduleCancellation
        );

        _ =
            DisposeScheduleAsync(
                previousScheduleTask,
                previousScheduleCancellation
            );

        _broadcastProbeService
            .ResetMatch(
                matchId
            );

        _liveDamageService
            .ResetMatch(
                matchId
            );

        if (matchId == 0)
        {
            _log(
                "Current match context: cleared"
            );

            return true;
        }

        _log(
            "Current match context: matchId=" +
            matchId
        );

        return true;
    }

    public bool NotifyCurrentHeroStatsReady(
        ulong expectedMatchId,
        DateTimeOffset generatedAtUtc
    )
    {
        ulong matchId;

        long generation;

        DateTimeOffset
            readyObservedAtUtc;

        DateTimeOffset
            scheduledStartAtUtc;

        CancellationTokenSource
            scheduleCancellation;

        lock (_stateGate)
        {
            if (
                _matchId == 0 ||
                _matchId !=
                    expectedMatchId ||
                _heroStatsReadyAtUtc
                    .HasValue
            )
            {
                return false;
            }

            matchId =
                _matchId;

            generation =
                _generation;

            readyObservedAtUtc =
                DateTimeOffset.UtcNow;

            _heroStatsGeneratedAtUtc =
                generatedAtUtc;

            _heroStatsReadyAtUtc =
                readyObservedAtUtc;

            scheduledStartAtUtc =
                readyObservedAtUtc +
                BroadcastProbeStartDelay;

            _broadcastProbeScheduledStartAtUtc =
                scheduledStartAtUtc;

            scheduleCancellation =
                new CancellationTokenSource();

            _scheduleCancellation =
                scheduleCancellation;

            /*
             * Publish the CTS and its task atomically. Otherwise a concurrent
             * match transition can dispose the CTS while the task has not yet
             * been created.
             */
            _scheduleTask =
                Task.Run(
                    () =>
                        RunScheduleAsync(
                            matchId,
                            generation,
                            scheduledStartAtUtc,
                            scheduleCancellation
                        )
                );
        }

        _broadcastProbeService
            .MarkScheduled(
                matchId,
                readyObservedAtUtc,
                scheduledStartAtUtc
            );

        _liveDamageService
            .MarkScheduled(
                matchId,
                readyObservedAtUtc,
                scheduledStartAtUtc
            );

        _log(
            "Current match broadcast probe: " +
            "CURRENT HERO STATS ready" +
            " | matchId=" +
            matchId +
            " | statsGeneratedAtUtc=" +
            generatedAtUtc.ToString("O") +
            " | readyObservedAtUtc=" +
            readyObservedAtUtc.ToString("O") +
            " | startAtUtc=" +
            scheduledStartAtUtc.ToString("O") +
            " | delay=00:00:05"
        );

        return true;
    }

    public CurrentMatchContextSnapshot
        GetSnapshot()
    {
        CurrentMatchContextSnapshot
            snapshot;

        lock (_stateGate)
        {
            snapshot =
                new CurrentMatchContextSnapshot(
                    MatchId:
                        _matchId,

                    HasMatch:
                        _matchId !=
                            0,

                    LastReceivedAtUtc:
                        _lastReceivedAtUtc,

                    MatchObservedAtUtc:
                        _matchObservedAtUtc,

                    HeroStatsGeneratedAtUtc:
                        _heroStatsGeneratedAtUtc,

                    HeroStatsReadyAtUtc:
                        _heroStatsReadyAtUtc,

                    BroadcastProbeScheduledStartAtUtc:
                        _broadcastProbeScheduledStartAtUtc,

                    BroadcastProbe:
                        CurrentMatchBroadcastProbeSnapshot
                            .Waiting,

                    LiveDamage:
                        CurrentMatchLiveDamageSnapshot
                            .Waiting
                );
        }

        return snapshot with
        {
            BroadcastProbe =
                _broadcastProbeService
                    .GetSnapshot(),

            LiveDamage =
                _liveDamageService
                    .GetSnapshot()
        };
    }

    public CurrentMatchLiveDamageSnapshot
        GetLiveDamageSnapshot()
    {
        return _liveDamageService
            .GetSnapshot();
    }

    private async Task RunScheduleAsync(
        ulong matchId,
        long generation,
        DateTimeOffset scheduledStartAtUtc,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            var delay =
                scheduledStartAtUtc -
                DateTimeOffset.UtcNow;

            if (
                delay >
                    TimeSpan.Zero
            )
            {
                await Task.Delay(
                    delay,
                    cancellation.Token
                );
            }

            lock (_stateGate)
            {
                if (
                    cancellation
                        .IsCancellationRequested ||
                    _matchId !=
                        matchId ||
                    _generation !=
                        generation ||
                    _broadcastProbeScheduledStartAtUtc !=
                        scheduledStartAtUtc
                )
                {
                    return;
                }
            }

            cancellation.Token
                .ThrowIfCancellationRequested();

            _log(
                "Current match broadcast probe: " +
                "5-second delay elapsed" +
                " | matchId=" +
                matchId
            );

            if (
                !_broadcastProbeService
                    .StartForMatch(
                        matchId
                    )
            )
            {
                _log(
                    "Current match broadcast probe: " +
                    "start rejected" +
                    " | matchId=" +
                    matchId
                );
            }
        }
        catch (
            OperationCanceledException
        )
        when (
            cancellation
                .IsCancellationRequested
        )
        {
        }
        finally
        {
            lock (_stateGate)
            {
                if (
                    ReferenceEquals(
                        _scheduleCancellation,
                        cancellation
                    )
                )
                {
                    _scheduleCancellation =
                        null;

                    _scheduleTask =
                        null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void OnBroadcastReady(
        CurrentMatchBroadcastReady ready
    )
    {
        var isCurrentMatch =
            false;

        lock (_stateGate)
        {
            isCurrentMatch =
                _matchId !=
                    0 &&
                _matchId ==
                    ready.MatchId;
        }

        if (!isCurrentMatch)
        {
            _log(
                "Current match live damage: " +
                "ignored stale broadcast READY" +
                " | matchId=" +
                ready.MatchId
            );

            return;
        }

        if (
            !_liveDamageService
                .StartForBroadcast(
                    ready.MatchId,
                    ready.BroadcastUrl,
                    ready.ReadyAtUtc
                )
        )
        {
            _log(
                "Current match live damage: " +
                "broadcast parser sidecar start rejected" +
                " | matchId=" +
                ready.MatchId
            );
        }
    }

    private static async Task DisposeScheduleAsync(
        Task? task,
        CancellationTokenSource? cancellation
    )
    {
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (
                OperationCanceledException
            )
            {
            }
            catch
            {
            }
        }

        /*
         * A running schedule disposes its own CTS
         * in RunScheduleAsync.finally.
         */
        if (task is null)
        {
            cancellation?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource?
            scheduleCancellation;

        Task?
            scheduleTask;

        lock (_stateGate)
        {
            _generation++;

            _matchId =
                0;

            _matchObservedAtUtc =
                null;

            scheduleCancellation =
                _scheduleCancellation;

            scheduleTask =
                _scheduleTask;

            _scheduleCancellation =
                null;

            _scheduleTask =
                null;
        }

        Cancel(
            scheduleCancellation
        );

        if (scheduleTask is not null)
        {
            try
            {
                await scheduleTask;
            }
            catch (
                OperationCanceledException
            )
            {
            }
            catch
            {
            }
        }
        else
        {
            scheduleCancellation?
                .Dispose();
        }

        await _broadcastProbeService
            .DisposeAsync();

        await _liveDamageService
            .DisposeAsync();
    }

    private static void Cancel(
        CancellationTokenSource?
            cancellation
    )
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (
            ObjectDisposedException
        )
        {
        }
    }
}

internal sealed record CurrentMatchContextSnapshot(
    ulong MatchId,
    bool HasMatch,
    DateTimeOffset? LastReceivedAtUtc,
    DateTimeOffset? MatchObservedAtUtc,
    DateTimeOffset? HeroStatsGeneratedAtUtc,
    DateTimeOffset? HeroStatsReadyAtUtc,
    DateTimeOffset? BroadcastProbeScheduledStartAtUtc,
    CurrentMatchBroadcastProbeSnapshot BroadcastProbe,
    CurrentMatchLiveDamageSnapshot LiveDamage
);
