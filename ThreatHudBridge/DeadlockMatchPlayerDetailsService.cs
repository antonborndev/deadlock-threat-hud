internal sealed class DeadlockMatchPlayerDetailsService :
    IAsyncDisposable
{
    private const int ExpectedPlayers =
        12;

    private readonly DeadlockHeroCatalogService
        _heroCatalogService;

    private readonly DeadlockPlayerStatsService
        _playerStatsService;

    private readonly CancellationToken
        _lifetimeToken;

    private readonly Action<
        DeadlockMatchPlayerDetailsService,
        DateTimeOffset
    >? _readyHandler;

    private readonly object _stateGate =
        new();

    private readonly List<Task>
        _runningTasks =
            new();

    private MatchPlayerDetailsRunState?
        _currentRunState;

    private string?
        _currentFingerprint;

    private DeadlockLaneAdvisorRosterRequest?
        _latestRoster;

    private uint _latestOwnAccountId;

    private long _generation;

    private DeadlockMatchPlayerDetailsSnapshot
        _snapshot =
            DeadlockMatchPlayerDetailsSnapshot
                .Waiting;

    private bool _disposed;

    public DeadlockMatchPlayerDetailsService(
        DeadlockHeroCatalogService heroCatalogService,
        DeadlockPlayerStatsService playerStatsService,
        CancellationToken lifetimeToken,
        Action<
            DeadlockMatchPlayerDetailsService,
            DateTimeOffset
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

        _lifetimeToken =
            lifetimeToken;

        _readyHandler =
            readyHandler;
    }

    public bool StartForRequests(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests
    )
    {
        ArgumentNullException.ThrowIfNull(
            requests
        );

        if (
            requests.Count < 1 ||
            requests.Count >
                ExpectedPlayers
        )
        {
            return false;
        }

        MatchPlayerDetailsRunState?
            previousState =
                null;

        MatchPlayerDetailsRunState
            currentState;

        CurrentMatchPlayerHeroRequest[]
            expandedRequests;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_latestRoster is not null)
            {
                if (
                    !TryExpandRequestsForRoster(
                        requests,
                        _latestRoster,
                        _latestOwnAccountId,
                        out expandedRequests
                    )
                )
                {
                    return false;
                }
            }
            else
            {
                if (
                    requests.Count !=
                        ExpectedPlayers
                )
                {
                    return false;
                }

                expandedRequests =
                    requests.ToArray();
            }

            ValidateRequests(
                expandedRequests
            );

            var fingerprint =
                BuildFingerprint(
                    expandedRequests
                );

            if (
                _currentRunState is not null &&
                String.Equals(
                    _currentFingerprint,
                    fingerprint,
                    StringComparison.Ordinal
                ) &&
                !_currentRunState.Failed
            )
            {
                return false;
            }

            _currentFingerprint =
                fingerprint;

            previousState =
                _currentRunState;

            currentState =
                new MatchPlayerDetailsRunState(
                    Generation:
                        ++_generation,

                    Cancellation:
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                _lifetimeToken
                            )
                );

            _currentRunState =
                currentState;

            _snapshot =
                new DeadlockMatchPlayerDetailsSnapshot(
                    Status:
                        "loading",

                    GeneratedAtUtc:
                        null,

                    Players:
                        Array.Empty<
                            DeadlockMatchPlayerDetailsEntry
                        >(),

                    Error:
                        null
                );

            _runningTasks.RemoveAll(
                task =>
                    task.IsCompleted
            );
        }

        if (previousState is not null)
        {
            CancelRunState(
                previousState
            );
        }

        var task =
            RunAsync(
                expandedRequests,
                currentState
            );

        lock (_stateGate)
        {
            if (_disposed)
            {
                CancelRunState(
                    currentState
                );

                return false;
            }

            _runningTasks.Add(
                task
            );
        }

        return true;
    }

    public bool StartForRoster(
        DeadlockLaneAdvisorRosterRequest roster,
        uint ownAccountId
    )
    {
        ArgumentNullException.ThrowIfNull(
            roster
        );

        if (
            roster.HeroNames.Count !=
                ExpectedPlayers ||
            roster.LocalIndex < 0 ||
            roster.LocalIndex >=
                ExpectedPlayers ||
            ownAccountId == 0
        )
        {
            return false;
        }

        var rosterSnapshot =
            new DeadlockLaneAdvisorRosterRequest(
                RosterVersion:
                    roster.RosterVersion,

                LocalIndex:
                    roster.LocalIndex,

                HeroNames:
                    roster.HeroNames.ToArray()
            );

        var fingerprint =
            BuildRosterFingerprint(
                rosterSnapshot,
                ownAccountId
            );

        MatchPlayerDetailsRunState?
            previousState =
                null;

        MatchPlayerDetailsRunState
            currentState;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _latestRoster =
                rosterSnapshot;

            _latestOwnAccountId =
                ownAccountId;

            if (
                _currentRunState is not null &&
                String.Equals(
                    _currentFingerprint,
                    fingerprint,
                    StringComparison.Ordinal
                ) &&
                !_currentRunState.Failed
            )
            {
                return false;
            }

            _currentFingerprint =
                fingerprint;

            previousState =
                _currentRunState;

            currentState =
                new MatchPlayerDetailsRunState(
                    Generation:
                        ++_generation,

                    Cancellation:
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                _lifetimeToken
                            )
                );

            _currentRunState =
                currentState;

            _snapshot =
                new DeadlockMatchPlayerDetailsSnapshot(
                    Status:
                        "loading",

                    GeneratedAtUtc:
                        null,

                    Players:
                        Array.Empty<
                            DeadlockMatchPlayerDetailsEntry
                        >(),

                    Error:
                        null
                );

            _runningTasks.RemoveAll(
                task =>
                    task.IsCompleted
            );
        }

        if (previousState is not null)
        {
            CancelRunState(
                previousState
            );
        }

        var task =
            RunRosterAsync(
                rosterSnapshot,
                ownAccountId,
                currentState
            );

        lock (_stateGate)
        {
            if (_disposed)
            {
                CancelRunState(
                    currentState
                );

                return false;
            }

            _runningTasks.Add(
                task
            );
        }

        return true;
    }

    public DeadlockMatchPlayerDetailsSnapshot
        GetSnapshot()
    {
        lock (_stateGate)
        {
            return _snapshot;
        }
    }

    private async Task RunRosterAsync(
        DeadlockLaneAdvisorRosterRequest roster,
        uint ownAccountId,
        MatchPlayerDetailsRunState state
    )
    {
        var cancellationToken =
            state.Cancellation.Token;

        try
        {
            var resolutions =
                await _heroCatalogService
                    .ResolveAsync(
                        roster.HeroNames,
                        cancellationToken
                    );

            if (
                resolutions.Count !=
                    ExpectedPlayers
            )
            {
                throw new InvalidOperationException(
                    "Hero catalog returned an invalid " +
                    "number of results."
                );
            }

            var heroes =
                await _heroCatalogService
                    .GetHeroesAsync(
                        cancellationToken
                    );

            var heroesById =
                heroes.ToDictionary(
                    hero =>
                        hero.Id
                );

            DeadlockHeroStats?
                localStats =
                    null;

            var localResolution =
                resolutions[
                    roster.LocalIndex
                ];

            if (
                localResolution.Status ==
                    "resolved" &&
                localResolution.HeroId.HasValue
            )
            {
                var localHeroId =
                    localResolution
                        .HeroId
                        .Value;

                var stats =
                    await _playerStatsService
                        .GetHeroStatsAsync(
                            new[]
                            {
                                ownAccountId
                            },
                            new[]
                            {
                                localHeroId
                            },
                            cancellationToken
                        );

                localStats =
                    stats
                        .Where(
                            row =>
                                row.AccountId ==
                                    ownAccountId &&
                                row.HeroId ==
                                    localHeroId
                        )
                        .OrderByDescending(
                            row =>
                                row.MatchesPlayed
                        )
                        .FirstOrDefault();
            }

            cancellationToken
                .ThrowIfCancellationRequested();

            var players =
                new List<
                    DeadlockMatchPlayerDetailsEntry
                >(
                    ExpectedPlayers
                );

            for (
                var index = 0;
                index < ExpectedPlayers;
                index++
            )
            {
                var resolution =
                    resolutions[index];

                if (
                    resolution.Status !=
                        "resolved" ||
                    !resolution.HeroId.HasValue
                )
                {
                    players.Add(
                        CreateRosterNoDataEntry(
                            index,
                            index ==
                                roster.LocalIndex
                                ? ownAccountId
                                : 0,
                            heroId:
                                0,
                            heroName:
                                roster.HeroNames[
                                    index
                                ],
                            heroIconUrl:
                                null,
                            status:
                                resolution.Status
                        )
                    );

                    continue;
                }

                var heroId =
                    resolution.HeroId.Value;

                heroesById.TryGetValue(
                    heroId,
                    out var heroAsset
                );

                var heroName =
                    resolution.ApiName ??
                    heroAsset?.Name ??
                    roster.HeroNames[index];

                var heroIconUrl =
                    heroAsset?
                        .Images?
                        .IconImageSmall;

                if (
                    index !=
                        roster.LocalIndex
                )
                {
                    players.Add(
                        CreateRosterNoDataEntry(
                            index,
                            accountId:
                                0,
                            heroId,
                            heroName,
                            heroIconUrl,
                            status:
                                "bot"
                        )
                    );

                    continue;
                }

                if (
                    localStats is null ||
                    localStats.MatchesPlayed ==
                        0
                )
                {
                    players.Add(
                        CreateRosterNoDataEntry(
                            index,
                            ownAccountId,
                            heroId,
                            heroName,
                            heroIconUrl,
                            status:
                                "stats-not-found"
                        )
                    );

                    continue;
                }

                var averagePlayerDamage =
                    (double)
                        localStats
                            .TotalPlayerDamage /
                    localStats
                        .MatchesPlayed;

                players.Add(
                    new DeadlockMatchPlayerDetailsEntry(
                        Index:
                            index,

                        AccountId:
                            ownAccountId,

                        HeroId:
                            heroId,

                        HeroName:
                            heroName,

                        HeroIconUrl:
                            heroIconUrl,

                        Status:
                            "ok",

                        MatchesPlayed:
                            localStats.MatchesPlayed,

                        Wins:
                            localStats.Wins,

                        WinRatePercent:
                            localStats.WinRatePercent,

                        AveragePlayerDamage:
                            averagePlayerDamage,

                        SoulsPerMinute:
                            localStats.NetworthPerMinute,

                        HeadshotRatePercent:
                            ToPercent(
                                localStats
                                    .CriticalShotRate
                            ),

                        AccuracyPercent:
                            ToPercent(
                                localStats.Accuracy
                            )
                    )
                );
            }

            var snapshot =
                new DeadlockMatchPlayerDetailsSnapshot(
                    Status:
                        "ready",

                    GeneratedAtUtc:
                        DateTimeOffset.UtcNow,

                    Players:
                        players,

                    Error:
                        null
                );

            var published =
                false;

            lock (_stateGate)
            {
                if (
                    !_disposed &&
                    ReferenceEquals(
                        _currentRunState,
                        state
                    ) &&
                    state.Generation ==
                        _generation
                )
                {
                    _snapshot =
                        snapshot;

                    state.Failed =
                        false;

                    published =
                        true;
                }
            }

            if (
                published &&
                snapshot.GeneratedAtUtc.HasValue
            )
            {
                NotifyReady(
                    snapshot.GeneratedAtUtc.Value
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
            MarkRunFailed(
                state,
                error:
                    null
            );
        }
        catch (Exception error)
        {
            MarkRunFailed(
                state,
                error.Message
            );
        }
        finally
        {
            state.Cancellation.Dispose();
        }
    }

    private async Task RunAsync(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests,
        MatchPlayerDetailsRunState state
    )
    {
        var cancellationToken =
            state.Cancellation.Token;

        try
        {
            var resolutions =
                await _heroCatalogService
                    .ResolveAsync(
                        requests.Select(
                            request =>
                                request.HeroName
                        ),
                        cancellationToken
                    );

            if (
                resolutions.Count !=
                    ExpectedPlayers
            )
            {
                throw new InvalidOperationException(
                    "Hero catalog returned an invalid " +
                    "number of results."
                );
            }

            var heroes =
                await _heroCatalogService
                    .GetHeroesAsync(
                        cancellationToken
                    );

            var heroesById =
                heroes.ToDictionary(
                    hero =>
                        hero.Id
                );

            var resolvedHeroIds =
                resolutions
                    .Select(
                        (
                            resolution,
                            index
                        ) =>
                            new
                            {
                                Resolution =
                                    resolution,

                                AccountId =
                                    requests[index]
                                        .AccountId
                            }
                    )
                    .Where(
                        item =>
                            item.AccountId != 0 &&
                            item.Resolution.Status ==
                                "resolved" &&
                            item.Resolution.HeroId
                                .HasValue
                    )
                    .Select(
                        item =>
                            item.Resolution.HeroId!
                                .Value
                    )
                    .Distinct()
                    .ToArray();

            var resolvedAccountIds =
                requests
                    .Where(
                        request =>
                            request.AccountId != 0
                    )
                    .Select(
                        request =>
                            request.AccountId
                    )
                    .Distinct()
                    .ToArray();

            IReadOnlyList<DeadlockHeroStats>
                stats;

            if (
                resolvedHeroIds.Length == 0 ||
                resolvedAccountIds.Length == 0
            )
            {
                stats =
                    Array.Empty<
                        DeadlockHeroStats
                    >();
            }
            else
            {
                stats =
                    await _playerStatsService
                        .GetHeroStatsAsync(
                            resolvedAccountIds,
                            resolvedHeroIds,
                            cancellationToken
                        );
            }

            cancellationToken
                .ThrowIfCancellationRequested();

            var statsByPair =
                stats
                    .GroupBy(
                        row =>
                            (
                                row.AccountId,
                                row.HeroId
                            )
                    )
                    .ToDictionary(
                        group =>
                            group.Key,
                        group =>
                            group
                                .OrderByDescending(
                                    row =>
                                        row.MatchesPlayed
                                )
                                .First()
                    );

            var players =
                new List<
                    DeadlockMatchPlayerDetailsEntry
                >(
                    ExpectedPlayers
                );

            for (
                var index = 0;
                index < requests.Count;
                index++
            )
            {
                var request =
                    requests[index];

                var resolution =
                    resolutions[index];

                if (
                    resolution.Status !=
                        "resolved" ||
                    !resolution.HeroId.HasValue
                )
                {
                    players.Add(
                        CreateNoDataEntry(
                            request,
                            heroId:
                                0,
                            heroName:
                                request.HeroName,
                            heroIconUrl:
                                null,
                            status:
                                request.AccountId == 0
                                    ? "identity-unresolved"
                                    : resolution.Status
                        )
                    );

                    continue;
                }

                var heroId =
                    resolution.HeroId.Value;

                heroesById.TryGetValue(
                    heroId,
                    out var heroAsset
                );

                var heroName =
                    resolution.ApiName ??
                    heroAsset?.Name ??
                    request.HeroName;

                var heroIconUrl =
                    heroAsset?
                        .Images?
                        .IconImageSmall;

                if (request.AccountId == 0)
                {
                    players.Add(
                        CreateNoDataEntry(
                            request,
                            heroId,
                            heroName,
                            heroIconUrl,
                            status:
                                "identity-unresolved"
                        )
                    );

                    continue;
                }

                if (
                    !statsByPair.TryGetValue(
                        (
                            request.AccountId,
                            heroId
                        ),
                        out var heroStats
                    ) ||
                    heroStats.MatchesPlayed ==
                        0
                )
                {
                    players.Add(
                        CreateNoDataEntry(
                            request,
                            heroId,
                            heroName,
                            heroIconUrl,
                            status:
                                "stats-not-found"
                        )
                    );

                    continue;
                }

                var averagePlayerDamage =
                    (double)
                        heroStats
                            .TotalPlayerDamage /
                    heroStats
                        .MatchesPlayed;

                players.Add(
                    new DeadlockMatchPlayerDetailsEntry(
                        Index:
                            request.Index,

                        AccountId:
                            request.AccountId,

                        HeroId:
                            heroId,

                        HeroName:
                            heroName,

                        HeroIconUrl:
                            heroIconUrl,

                        Status:
                            "ok",

                        MatchesPlayed:
                            heroStats.MatchesPlayed,

                        Wins:
                            heroStats.Wins,

                        WinRatePercent:
                            heroStats.WinRatePercent,

                        AveragePlayerDamage:
                            averagePlayerDamage,

                        SoulsPerMinute:
                            heroStats.NetworthPerMinute,

                        HeadshotRatePercent:
                            ToPercent(
                                heroStats
                                    .CriticalShotRate
                            ),

                        AccuracyPercent:
                            ToPercent(
                                heroStats.Accuracy
                            )
                    )
                );
            }

            var snapshot =
                new DeadlockMatchPlayerDetailsSnapshot(
                    Status:
                        "ready",

                    GeneratedAtUtc:
                        DateTimeOffset.UtcNow,

                    Players:
                        players,

                    Error:
                        null
                );

            var published =
                false;

            lock (_stateGate)
            {
                if (
                    !_disposed &&
                    ReferenceEquals(
                        _currentRunState,
                        state
                    ) &&
                    state.Generation ==
                        _generation
                )
                {
                    _snapshot =
                        snapshot;

                    state.Failed =
                        false;

                    published =
                        true;
                }
            }

            if (
                published &&
                snapshot.GeneratedAtUtc.HasValue
            )
            {
                NotifyReady(
                    snapshot.GeneratedAtUtc.Value
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
            MarkRunFailed(
                state,
                error:
                    null
            );
        }
        catch (Exception error)
        {
            MarkRunFailed(
                state,
                error.Message
            );
        }
        finally
        {
            state.Cancellation.Dispose();
        }
    }

    private static DeadlockMatchPlayerDetailsEntry
        CreateRosterNoDataEntry(
            int index,
            uint accountId,
            uint heroId,
            string heroName,
            string? heroIconUrl,
            string status
        )
    {
        return new DeadlockMatchPlayerDetailsEntry(
            Index:
                index,

            AccountId:
                accountId,

            HeroId:
                heroId,

            HeroName:
                heroName,

            HeroIconUrl:
                heroIconUrl,

            Status:
                status,

            MatchesPlayed:
                0,

            Wins:
                0,

            WinRatePercent:
                0,

            AveragePlayerDamage:
                0,

            SoulsPerMinute:
                0,

            HeadshotRatePercent:
                0,

            AccuracyPercent:
                0
        );
    }

    private static DeadlockMatchPlayerDetailsEntry
        CreateNoDataEntry(
            CurrentMatchPlayerHeroRequest request,
            uint heroId,
            string heroName,
            string? heroIconUrl,
            string status
        )
    {
        return new DeadlockMatchPlayerDetailsEntry(
            Index:
                request.Index,

            AccountId:
                request.AccountId,

            HeroId:
                heroId,

            HeroName:
                heroName,

            HeroIconUrl:
                heroIconUrl,

            Status:
                status,

            MatchesPlayed:
                0,

            Wins:
                0,

            WinRatePercent:
                0,

            AveragePlayerDamage:
                0,

            SoulsPerMinute:
                0,

            HeadshotRatePercent:
                0,

            AccuracyPercent:
                0
        );
    }

    private static double ToPercent(
        double value
    )
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        return Math.Clamp(
            value * 100.0,
            0,
            100
        );
    }

    private static bool TryExpandRequestsForRoster(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > compactRequests,
        DeadlockLaneAdvisorRosterRequest roster,
        uint ownAccountId,
        out CurrentMatchPlayerHeroRequest[]
            expandedRequests
    )
    {
        expandedRequests =
            Array.Empty<
                CurrentMatchPlayerHeroRequest
            >();

        if (
            compactRequests.Count < 1 ||
            compactRequests.Count >
                ExpectedPlayers ||
            roster.HeroNames.Count !=
                ExpectedPlayers ||
            roster.LocalIndex < 0 ||
            roster.LocalIndex >=
                ExpectedPlayers ||
            ownAccountId == 0
        )
        {
            return false;
        }

        var rosterIndexesByHero =
            new Dictionary<string, int>(
                StringComparer.Ordinal
            );

        var expanded =
            new CurrentMatchPlayerHeroRequest[
                ExpectedPlayers
            ];

        for (
            var index = 0;
            index < ExpectedPlayers;
            index++
        )
        {
            var heroName =
                roster.HeroNames[index];

            var normalizedHeroName =
                DeadlockHeroCatalogService
                    .NormalizeName(
                        heroName
                    );

            if (
                normalizedHeroName.Length == 0 ||
                !rosterIndexesByHero.TryAdd(
                    normalizedHeroName,
                    index
                )
            )
            {
                return false;
            }

            expanded[index] =
                new CurrentMatchPlayerHeroRequest(
                    Index:
                        index,

                    AccountId:
                        0,

                    HeroName:
                        heroName
                );
        }

        var assignedRosterIndexes =
            new HashSet<int>();

        var accountIds =
            new HashSet<uint>();

        foreach (
            var request in compactRequests
        )
        {
            var normalizedHeroName =
                DeadlockHeroCatalogService
                    .NormalizeName(
                        request.HeroName
                    );

            if (
                request.AccountId == 0 ||
                normalizedHeroName.Length == 0 ||
                !rosterIndexesByHero.TryGetValue(
                    normalizedHeroName,
                    out var rosterIndex
                ) ||
                !assignedRosterIndexes.Add(
                    rosterIndex
                ) ||
                !accountIds.Add(
                    request.AccountId
                )
            )
            {
                return false;
            }

            expanded[rosterIndex] =
                new CurrentMatchPlayerHeroRequest(
                    Index:
                        rosterIndex,

                    AccountId:
                        request.AccountId,

                    HeroName:
                        roster.HeroNames[
                            rosterIndex
                        ]
                );
        }

        var localRequest =
            expanded[
                roster.LocalIndex
            ];

        if (localRequest.AccountId == 0)
        {
            if (
                !accountIds.Add(
                    ownAccountId
                )
            )
            {
                return false;
            }

            expanded[
                roster.LocalIndex
            ] =
                localRequest with
                {
                    AccountId =
                        ownAccountId
                };
        }
        else if (
            localRequest.AccountId !=
                ownAccountId
        )
        {
            return false;
        }

        expandedRequests =
            expanded;

        return true;
    }

    private static void ValidateRequests(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests
    )
    {
        if (
            requests.Count !=
                ExpectedPlayers
        )
        {
            throw new InvalidOperationException(
                "Match player details require " +
                "exactly 12 roster slots."
            );
        }

        var accountIds =
            new HashSet<uint>();

        for (
            var index = 0;
            index < requests.Count;
            index++
        )
        {
            var request =
                requests[index];

            if (
                request.Index !=
                    index
            )
            {
                throw new InvalidOperationException(
                    "Match player details require " +
                    "ordered roster indexes 0..11."
                );
            }

            if (
                request.AccountId != 0 &&
                !accountIds.Add(
                    request.AccountId
                )
            )
            {
                throw new InvalidOperationException(
                    "Match player details received " +
                    "a duplicate account ID."
                );
            }

            if (
                String.IsNullOrWhiteSpace(
                    request.HeroName
                )
            )
            {
                throw new InvalidOperationException(
                    "Match player details received " +
                    "an empty hero name."
                );
            }
        }

        if (accountIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Match player details received no " +
                "resolved account IDs."
            );
        }
    }

    private static string BuildRosterFingerprint(
        DeadlockLaneAdvisorRosterRequest roster,
        uint ownAccountId
    )
    {
        return (
            "roster:" +
            ownAccountId +
            ":" +
            roster.LocalIndex +
            "|" +
            String.Join(
                "|",
                roster.HeroNames.Select(
                    heroName =>
                        DeadlockHeroCatalogService
                            .NormalizeName(
                                heroName
                            )
                )
            )
        );
    }

    private static string BuildFingerprint(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests
    )
    {
        return String.Join(
            "|",
            requests.Select(
                request =>
                    request.Index +
                    ":" +
                    request.AccountId +
                    ":" +
                    DeadlockHeroCatalogService
                        .NormalizeName(
                            request.HeroName
                        )
            )
        );
    }

    private void NotifyReady(
        DateTimeOffset generatedAtUtc
    )
    {
        if (_readyHandler is null)
        {
            return;
        }

        try
        {
            _readyHandler(
                this,
                generatedAtUtc
            );
        }
        catch
        {
            // The snapshot is already ready. A consumer
            // callback must not turn it into a failed run.
        }
    }

    private void MarkRunFailed(
        MatchPlayerDetailsRunState state,
        string? error
    )
    {
        lock (_stateGate)
        {
            if (
                _disposed ||
                !ReferenceEquals(
                    _currentRunState,
                    state
                ) ||
                state.Generation !=
                    _generation
            )
            {
                return;
            }

            state.Failed =
                true;

            if (
                !String.IsNullOrWhiteSpace(
                    error
                )
            )
            {
                _snapshot =
                    new DeadlockMatchPlayerDetailsSnapshot(
                        Status:
                            "failed",

                        GeneratedAtUtc:
                            DateTimeOffset.UtcNow,

                        Players:
                            Array.Empty<
                                DeadlockMatchPlayerDetailsEntry
                            >(),

                        Error:
                            error
                    );
            }
        }
    }

    private static void CancelRunState(
        MatchPlayerDetailsRunState state
    )
    {
        try
        {
            state.Cancellation.Cancel();
        }
        catch (
            ObjectDisposedException
        )
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;

        MatchPlayerDetailsRunState?
            currentState;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed =
                true;

            currentState =
                _currentRunState;

            _currentRunState =
                null;

            tasks =
                _runningTasks
                    .ToArray();
        }

        if (currentState is not null)
        {
            CancelRunState(
                currentState
            );
        }

        if (
            tasks.Length ==
                0
        )
        {
            return;
        }

        try
        {
            await Task.WhenAll(
                tasks
            );
        }
        catch (
            OperationCanceledException
        )
        {
        }
    }

    private sealed class MatchPlayerDetailsRunState
    {
        public MatchPlayerDetailsRunState(
            long Generation,
            CancellationTokenSource Cancellation
        )
        {
            this.Generation =
                Generation;

            this.Cancellation =
                Cancellation;
        }

        public long Generation
        {
            get;
        }

        public CancellationTokenSource Cancellation
        {
            get;
        }

        public bool Failed
        {
            get;
            set;
        }
    }
}

internal sealed record DeadlockMatchPlayerDetailsSnapshot(
    string Status,
    DateTimeOffset? GeneratedAtUtc,
    IReadOnlyList<
        DeadlockMatchPlayerDetailsEntry
    > Players,
    string? Error
)
{
    public static DeadlockMatchPlayerDetailsSnapshot
        Waiting =>
            new(
                Status:
                    "waiting",

                GeneratedAtUtc:
                    null,

                Players:
                    Array.Empty<
                        DeadlockMatchPlayerDetailsEntry
                    >(),

                Error:
                    null
            );
}

internal sealed record DeadlockMatchPlayerDetailsEntry(
    int Index,
    uint AccountId,
    uint HeroId,
    string HeroName,
    string? HeroIconUrl,
    string Status,
    ulong MatchesPlayed,
    ulong Wins,
    double WinRatePercent,
    double AveragePlayerDamage,
    double SoulsPerMinute,
    double HeadshotRatePercent,
    double AccuracyPercent
);
