internal sealed class CurrentMatchPlayerDetailsCoordinator :
    IAsyncDisposable
{
    private readonly DeadlockHeroCatalogService
        _heroCatalogService;

    private readonly DeadlockPlayerStatsService
        _playerStatsService;

    private readonly DeadlockPlayerRankService
        _playerRankService;

    private readonly CancellationToken
        _lifetimeToken;

    private readonly Action<string> _log;

    private readonly Action<
        CurrentMatchPlayerDetailsReady
    >? _readyHandler;

    private readonly object _stateGate =
        new();

    private readonly List<Task>
        _retiredDisposalTasks =
            new();

    private DeadlockMatchPlayerDetailsService
        _currentService;

    private ulong _matchId;

    private bool _serviceIsUnassigned =
        true;

    private bool _readyNotified;

    private bool _disposed;

    public CurrentMatchPlayerDetailsCoordinator(
        DeadlockHeroCatalogService heroCatalogService,
        DeadlockPlayerStatsService playerStatsService,
        DeadlockPlayerRankService playerRankService,
        CancellationToken lifetimeToken,
        Action<string>? log = null,
        Action<
            CurrentMatchPlayerDetailsReady
        >? readyHandler = null
    )
    {
        _heroCatalogService =
            heroCatalogService ??
            throw new ArgumentNullException(
                nameof(heroCatalogService)
            );

        _playerStatsService =
            playerStatsService ??
            throw new ArgumentNullException(
                nameof(playerStatsService)
            );

        _playerRankService =
            playerRankService ??
            throw new ArgumentNullException(
                nameof(playerRankService)
            );

        _lifetimeToken =
            lifetimeToken;

        _log =
            log ??
            (_ => { });

        _readyHandler =
            readyHandler;

        _currentService =
            CreateService();
    }

    public bool TransitionToMatch(
        ulong matchId
    )
    {
        DeadlockMatchPlayerDetailsService?
            previousService =
                null;

        var serviceReplaced =
            false;

        CurrentMatchPlayerDetailsReady?
            readyNotification =
                null;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            /*
             * After a clear, the current service is already a fresh,
             * unassigned epoch. Assigning the next non-zero match ID must
             * not cancel requests that may have started just before the
             * match-context reporter observed that ID.
             */
            if (
                matchId != 0 &&
                _serviceIsUnassigned
            )
            {
                _matchId =
                    matchId;

                _serviceIsUnassigned =
                    false;

                var snapshot =
                    _currentService
                        .GetSnapshot();

                if (
                    !_readyNotified &&
                    String.Equals(
                        snapshot.Status,
                        "ready",
                        StringComparison.Ordinal
                    ) &&
                    snapshot.GeneratedAtUtc
                        .HasValue &&
                    snapshot.Players.Count ==
                        12
                )
                {
                    _readyNotified =
                        true;

                    readyNotification =
                        new CurrentMatchPlayerDetailsReady(
                            matchId,
                            snapshot.GeneratedAtUtc.Value
                        );
                }
            }
            else
            {
                if (
                    _matchId ==
                        matchId
                )
                {
                    return false;
                }

                _matchId =
                    matchId;

                _serviceIsUnassigned =
                    matchId == 0;

                _readyNotified =
                    false;

                previousService =
                    _currentService;

                _currentService =
                    CreateService();

                serviceReplaced =
                    true;

                _retiredDisposalTasks.RemoveAll(
                    task =>
                        task.IsCompleted
                );

                _retiredDisposalTasks.Add(
                    DisposeRetiredServiceAsync(
                        previousService
                    )
                );
            }
        }

        _log(
            "Current match player details: " +
            (
                serviceReplaced
                    ? "reset"
                    : "assigned"
            ) +
            " | matchId=" +
            matchId
        );

        NotifyReady(
            readyNotification
        );

        return true;
    }

    public bool StartForRequests(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests,
        bool includeRank
    )
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            return _currentService
                .StartForRequests(
                    requests,
                    includeRank
                );
        }
    }

    public bool StartForRoster(
        DeadlockLaneAdvisorRosterRequest roster,
        uint ownAccountId,
        bool includeRank
    )
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            return _currentService
                .StartForRoster(
                    roster,
                    ownAccountId,
                    includeRank
                );
        }
    }

    public DeadlockMatchPlayerDetailsSnapshot
        GetSnapshotForMatch(
            ulong expectedMatchId
        )
    {
        lock (_stateGate)
        {
            if (
                _disposed ||
                expectedMatchId == 0 ||
                _serviceIsUnassigned ||
                _matchId !=
                    expectedMatchId
            )
            {
                return DeadlockMatchPlayerDetailsSnapshot
                    .Waiting;
            }

            return _currentService
                .GetSnapshot();
        }
    }

    public bool ApplyRankSnapshot(
        CurrentMatchPlayerRanksSnapshot snapshot,
        bool includeRank
    )
    {
        ArgumentNullException.ThrowIfNull(
            snapshot
        );

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            return _currentService
                .ApplyRankSnapshot(
                    snapshot,
                    includeRank
                );
        }
    }

    public bool RefreshRanksForMatch(
        ulong expectedMatchId
    )
    {
        lock (_stateGate)
        {
            if (
                _disposed ||
                expectedMatchId == 0 ||
                _serviceIsUnassigned ||
                _matchId !=
                    expectedMatchId
            )
            {
                return false;
            }

            return _currentService
                .StartRankRefresh();
        }
    }

    private DeadlockMatchPlayerDetailsService
        CreateService()
    {
        return new DeadlockMatchPlayerDetailsService(
            _heroCatalogService,
            _playerStatsService,
            _playerRankService,
            _lifetimeToken,
            OnServiceReady
        );
    }

    private void OnServiceReady(
        DeadlockMatchPlayerDetailsService service,
        DateTimeOffset generatedAtUtc
    )
    {
        CurrentMatchPlayerDetailsReady?
            readyNotification =
                null;

        lock (_stateGate)
        {
            if (
                _disposed ||
                _serviceIsUnassigned ||
                _matchId == 0 ||
                _readyNotified ||
                !ReferenceEquals(
                    _currentService,
                    service
                )
            )
            {
                return;
            }

            _readyNotified =
                true;

            readyNotification =
                new CurrentMatchPlayerDetailsReady(
                    _matchId,
                    generatedAtUtc
                );
        }

        NotifyReady(
            readyNotification
        );
    }

    private void NotifyReady(
        CurrentMatchPlayerDetailsReady?
            ready
    )
    {
        if (
            ready is null ||
            _readyHandler is null
        )
        {
            return;
        }

        try
        {
            _readyHandler(
                ready
            );
        }
        catch (Exception error)
        {
            _log(
                "Current match player details: " +
                "ready handler ERROR" +
                " | matchId=" +
                ready.MatchId +
                " | " +
                error.Message
            );
        }
    }

    private async Task DisposeRetiredServiceAsync(
        DeadlockMatchPlayerDetailsService service
    )
    {
        try
        {
            await service.DisposeAsync();
        }
        catch (Exception error)
        {
            _log(
                "Current match player details: " +
                "retired service disposal ERROR" +
                " | " +
                error.Message
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        DeadlockMatchPlayerDetailsService
            currentService;

        Task[] retiredTasks;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed =
                true;

            currentService =
                _currentService;

            retiredTasks =
                _retiredDisposalTasks
                    .ToArray();

            _retiredDisposalTasks.Clear();
        }

        await DisposeRetiredServiceAsync(
            currentService
        );

        if (retiredTasks.Length > 0)
        {
            await Task.WhenAll(
                retiredTasks
            );
        }
    }
}

internal sealed record CurrentMatchPlayerDetailsReady(
    ulong MatchId,
    DateTimeOffset GeneratedAtUtc
);
