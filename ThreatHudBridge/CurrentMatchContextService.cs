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

    private bool
        _heroDamageAllowedForMatch;

    private bool?
        _localPlayerWon;

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
        ulong matchId,
        bool heroDamageEnabledAtMatchStart
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

                _localPlayerWon =
                    null;

                _matchObservedAtUtc =
                    matchId == 0
                        ? null
                        : receivedAtUtc;

                _generation++;

                /*
                 * The Hero Damage decision is latched once per match. Turning
                 * the checkbox back on later must not start a network workflow
                 * in the middle of that match.
                 */
                _heroDamageAllowedForMatch =
                    matchId != 0 &&
                    heroDamageEnabledAtMatchStart;

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
            matchId +
            " | heroDamage=" +
            (
                heroDamageEnabledAtMatchStart
                    ? "allowed"
                    : "blocked"
            )
        );

        return true;
    }

    public bool TrySetLocalPlayerWon(
        ulong expectedMatchId,
        bool localPlayerWon,
        out bool changed
    )
    {
        lock (_stateGate)
        {
            changed =
                false;

            if (
                _matchId == 0 ||
                _matchId !=
                    expectedMatchId
            )
            {
                return false;
            }

            if (!_localPlayerWon.HasValue)
            {
                _localPlayerWon =
                    localPlayerWon;

                changed =
                    true;

                return true;
            }

            return _localPlayerWon.Value ==
                localPlayerWon;
        }
    }

    public bool DisableHeroDamageForCurrentMatch()
    {
        ulong matchId;

        CancellationTokenSource?
            scheduleCancellation;

        Task?
            scheduleTask;

        lock (_stateGate)
        {
            if (
                _matchId == 0 ||
                !_heroDamageAllowedForMatch
            )
            {
                return false;
            }

            matchId =
                _matchId;

            _heroDamageAllowedForMatch =
                false;

            /*
             * Invalidating the generation prevents the delayed task from
             * starting the URL probe even when cancellation races with the
             * end of its five-second delay.
             */
            _generation++;

            _broadcastProbeScheduledStartAtUtc =
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

        _ =
            DisposeScheduleAsync(
                scheduleTask,
                scheduleCancellation
            );

        /*
         * ResetMatch(0) is the broadcast probe's existing hard-stop path. It
         * cancels a pending/in-flight stream URL probe and makes a late READY
         * callback stale. Live damage has a match-aware stop so a stale start
         * is rejected even if it races with this method.
         */
        _broadcastProbeService
            .ResetMatch(
                0
            );

        _liveDamageService
            .DisableForMatch(
                matchId
            );

        _log(
            "Current match Hero Damage: disabled for the remainder of match" +
            " | matchId=" +
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
                !_heroDamageAllowedForMatch ||
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

                    LocalPlayerWon:
                        _localPlayerWon,

                    HeroDamageAllowedForMatch:
                        _heroDamageAllowedForMatch,

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
                    !_heroDamageAllowedForMatch ||
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
        var canStart =
            false;

        lock (_stateGate)
        {
            canStart =
                _matchId !=
                    0 &&
                _matchId ==
                    ready.MatchId &&
                _heroDamageAllowedForMatch;
        }

        if (!canStart)
        {
            _log(
                "Current match live damage: " +
                "ignored stale or disabled broadcast READY" +
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

            _heroDamageAllowedForMatch =
                false;

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
    bool? LocalPlayerWon,
    bool HeroDamageAllowedForMatch,
    DateTimeOffset? LastReceivedAtUtc,
    DateTimeOffset? MatchObservedAtUtc,
    DateTimeOffset? HeroStatsGeneratedAtUtc,
    DateTimeOffset? HeroStatsReadyAtUtc,
    DateTimeOffset? BroadcastProbeScheduledStartAtUtc,
    CurrentMatchBroadcastProbeSnapshot BroadcastProbe,
    CurrentMatchLiveDamageSnapshot LiveDamage
);
