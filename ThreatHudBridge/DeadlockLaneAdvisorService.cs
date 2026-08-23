using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

internal sealed class DeadlockLaneAdvisorService :
    IAsyncDisposable
{
    public const string Channel =
        "lane-advisor-roster";

    public const int ExpectedPlayers =
        12;

    private const int TeamSize =
        6;

    private const int ExpectedOptions =
        5;

    private const int MaximumDiagnosticEvents =
        500;

    private const int TransportOptionBytes =
        16;

    private const byte TransportStatusPending =
        0;

    private const byte TransportStatusReady =
        1;

    private const byte TransportStatusFailed =
        2;

    private const byte TransportSwapStay =
        byte.MaxValue;

    private const byte TransportFlagHasMatchData =
        1 << 0;

    private const byte TransportFlagHasNetWorthData =
        1 << 1;

    /*
     * The option is the calculated BEST.
     *
     * Panorama does not recalculate scoring —
     * it only visualizes the Bridge decision.
     */
    private const byte TransportFlagIsBest =
        1 << 2;

    private const double ConfidencePriorMatches =
        20.0;

    private const ulong MinimumRecommendationMatches =
        10;

    private const ulong MinimumLowConfidenceMatches =
        5;

    private const ulong HighConfidenceMatches =
        30;

    private readonly DeadlockHeroCatalogService
        _heroCatalogService;

    private readonly DeadlockApiClient
        _apiClient;

    private readonly CancellationToken
        _lifetimeToken;

    private readonly object _stateGate =
        new();

    private readonly ConcurrentQueue<
        DeadlockLaneAdvisorDiagnosticEvent
    > _events =
        new();

    private readonly List<Task>
        _runningTasks =
            new();

    private LaneAdvisorRunState?
        _currentRunState;

    private string?
        _currentFingerprint;

    private long _latestRosterVersion;

    private long _generation;
    private long _nextEventId;

    private bool _disposed;

    public DeadlockLaneAdvisorService(
        DeadlockHeroCatalogService heroCatalogService,
        DeadlockApiClient apiClient,
        CancellationToken lifetimeToken
    )
    {
        _heroCatalogService =
            heroCatalogService ??
            throw new ArgumentNullException(
                nameof(heroCatalogService)
            );

        _apiClient =
            apiClient ??
            throw new ArgumentNullException(
                nameof(apiClient)
            );

        _lifetimeToken =
            lifetimeToken;
    }

    public bool StartForRoster(
        DeadlockLaneAdvisorRosterRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(
            request
        );

        var fingerprint =
            BuildFingerprint(
                request
            );

        LaneAdvisorRunState?
            previousState =
                null;

        LaneAdvisorRunState
            currentState;

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (
                request.RosterVersion <=
                    _latestRosterVersion
            )
            {
                return false;
            }

            _latestRosterVersion =
                request.RosterVersion;

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
                _currentRunState.RosterVersion =
                    request.RosterVersion;

                return false;
            }

            _currentFingerprint =
                fingerprint;

            var generation =
                ++_generation;

            previousState =
                _currentRunState;

            currentState =
                new LaneAdvisorRunState(
                    Generation:
                        generation,

                    RosterVersion:
                        request.RosterVersion,

                    Cancellation:
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                _lifetimeToken
                            )
                );

            _currentRunState =
                currentState;

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

        Publish(
            currentState.Generation,

            "LANE ADVISOR: START" +
            $" | generation={currentState.Generation}" +
            $" | rosterVersion={request.RosterVersion}" +
            $" | localIndex={request.LocalIndex}"
        );

        var task =
            RunAsync(
                request,
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

    public byte[] BuildResultPacket(
        long rosterVersion
    )
    {
        return BuildResultPacket(
            rosterVersion,
            out _
        );
    }

    public byte[] BuildResultPacket(
        long rosterVersion,
        out DeadlockLaneAdvisorResultState resultState
    )
    {
        lock (_stateGate)
        {
            if (
                _disposed ||
                _currentRunState is null ||
                _currentRunState.RosterVersion !=
                    rosterVersion
            )
            {
                resultState =
                    DeadlockLaneAdvisorResultState
                        .Failed;

                return BuildFailurePacket();
            }

            if (_currentRunState.Failed)
            {
                resultState =
                    DeadlockLaneAdvisorResultState
                        .Failed;

                return BuildFailurePacket();
            }

            if (
                _currentRunState.Packet is not null
            )
            {
                resultState =
                    DeadlockLaneAdvisorResultState
                        .Ready;

                return _currentRunState.Packet;
            }

            resultState =
                DeadlockLaneAdvisorResultState
                    .Pending;

            return BuildPendingPacket();
        }
    }

    public IReadOnlyList<
        DeadlockLaneAdvisorDiagnosticEvent
    > GetDiagnosticEventsAfter(
        long afterEventId
    )
    {
        return _events
            .Where(
                item =>
                    item.Id >
                        afterEventId
            )
            .OrderBy(
                item =>
                    item.Id
            )
            .ToArray();
    }

    private async Task RunAsync(
        DeadlockLaneAdvisorRosterRequest roster,
        LaneAdvisorRunState state
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

            var unresolvedIndexes =
                Enumerable.Range(
                    0,
                    ExpectedPlayers
                )
                .Where(
                    index =>
                        resolutions[index]
                            .Status !=
                                "resolved" ||
                        !resolutions[index]
                            .HeroId
                            .HasValue
                )
                .ToArray();

            if (
                unresolvedIndexes.Length >
                    0
            )
            {
                Publish(
                    state.Generation,

                    "LANE ADVISOR: STOP" +
                    " | unresolvedHeroIndexes=" +
                    string.Join(
                        ",",
                        unresolvedIndexes
                    )
                );

                MarkRunFailed(
                    state
                );

                return;
            }

            var heroIds =
                resolutions
                    .Select(
                        resolution =>
                            resolution
                                .HeroId!
                                .Value
                    )
                    .ToArray();

            var heroNames =
                resolutions
                    .Select(
                        resolution =>
                            resolution.ApiName ??
                            resolution.InputName
                    )
                    .ToArray();

            var matchupRows =
                await _apiClient
                    .GetLaneMatchupStatsAsync(
                        heroIds.Take(
                            TeamSize
                        ),

                        heroIds.Skip(
                            TeamSize
                        ),

                        minMatches:
                            1,

                        cancellationToken
                    );

            cancellationToken
                .ThrowIfCancellationRequested();

            var result =
                BuildResult(
                    state.Generation,
                    roster.LocalIndex,
                    heroIds,
                    heroNames,
                    matchupRows
                );

            Publish(
                state.Generation,
                BuildDiagnosticLines(
                    result
                )
            );

            var packet =
                BuildReadyPacket(
                    result
                );

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
                    state.Packet =
                        packet;

                    state.Failed =
                        false;
                }
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
                state
            );
        }
        catch (Exception error)
        {
            Publish(
                state.Generation,

                "LANE ADVISOR: ERROR" +
                $" | generation={state.Generation}" +
                " | " +
                error.Message
            );

            MarkRunFailed(
                state
            );
        }
        finally
        {
            state.Cancellation.Dispose();
        }
    }

    private static LaneAdvisorCalculationResult BuildResult(
        long generation,
        int localIndex,
        IReadOnlyList<uint> heroIds,
        IReadOnlyList<string> heroNames,
        IReadOnlyList<
            DeadlockLaneMatchupStats
        > matchupRows
    )
    {
        var options =
            new List<
                LaneAdvisorOptionResult
            >(
                ExpectedOptions
            );

        var bestOptionIndex =
            -1;

        var bestScore =
            double.NegativeInfinity;

        foreach (
            var option in
            BuildOptions(
                localIndex
            )
        )
        {
            var label =
                option.SwapWithIndex.HasValue
                    ? "SWAP [" +
                        option
                            .SwapWithIndex
                            .Value +
                        "] " +
                        heroNames[
                            option
                                .SwapWithIndex
                                .Value
                        ]
                    : "STAY";

            var rows =
                matchupRows
                    .Where(
                        row =>
                            PairMatches(
                                row.HeroIds,
                                heroIds[
                                    localIndex
                                ],
                                heroIds[
                                    option
                                        .TeammateIndex
                                ]
                            ) &&
                            PairMatches(
                                row.EnemyHeroIds,
                                heroIds[
                                    option
                                        .EnemyIndex1
                                ],
                                heroIds[
                                    option
                                        .EnemyIndex2
                                ]
                            )
                    )
                    .ToArray();

            ulong matches =
                0;

            ulong wins =
                0;

            ulong netWorthMatches =
                0;

            double weightedNetWorth =
                0;

            for (
                var index = 0;
                index < rows.Length;
                index++
            )
            {
                var row =
                    rows[index];

                matches =
                    checked(
                        matches +
                        row.MatchesPlayed
                    );

                wins =
                    checked(
                        wins +
                        row.Wins
                    );

                netWorthMatches =
                    checked(
                        netWorthMatches +
                        row.NetWorthMatches
                    );

                weightedNetWorth +=
                    row.NetWorthDiff15Min *
                    row.NetWorthMatches;
            }

            var hasMatchData =
                rows.Length > 0 &&
                matches > 0;

            var hasNetWorthData =
                netWorthMatches > 0;

            var netWorthDiff15 =
                hasNetWorthData
                    ? weightedNetWorth /
                        netWorthMatches
                    : 0;

            var confidenceWeight =
                hasNetWorthData
                    ? (double)netWorthMatches /
                        (
                            netWorthMatches +
                            ConfidencePriorMatches
                        )
                    : 0;

            var confidenceLevel =
                !hasNetWorthData ||
                netWorthMatches <
                    MinimumLowConfidenceMatches
                    ? "INSUFFICIENT"
                    : netWorthMatches <
                        MinimumRecommendationMatches
                        ? "LOW"
                        : netWorthMatches <
                            HighConfidenceMatches
                            ? "MEDIUM"
                            : "HIGH";

            var eligible =
                hasNetWorthData &&
                netWorthMatches >=
                    MinimumRecommendationMatches;

            var adjustedScore =
                hasNetWorthData
                    ? netWorthDiff15 *
                        confidenceWeight
                    : 0;

            var result =
                new LaneAdvisorOptionResult(
                    Option:
                        option,

                    Label:
                        label,

                    LaneRows:
                        rows.Length,

                    HasMatchData:
                        hasMatchData,

                    Matches:
                        matches,

                    Wins:
                        wins,

                    HasNetWorthData:
                        hasNetWorthData,

                    NetWorthDiff15:
                        netWorthDiff15,

                    NetWorthMatches:
                        netWorthMatches,

                    ConfidenceWeight:
                        confidenceWeight,

                    ConfidenceLevel:
                        confidenceLevel,

                    Eligible:
                        eligible,

                    AdjustedScore:
                        adjustedScore
                );

            options.Add(
                result
            );

            if (
                eligible &&
                adjustedScore >
                    bestScore
            )
            {
                bestScore =
                    adjustedScore;

                bestOptionIndex =
                    options.Count -
                    1;
            }
        }

        if (
            options.Count !=
                ExpectedOptions
        )
        {
            throw new InvalidOperationException(
                "Lane Advisor produced an invalid " +
                $"number of options: {options.Count}."
            );
        }

        double? improvementVsStay =
            null;

        if (
            bestOptionIndex >= 0 &&
            options[0].Eligible
        )
        {
            improvementVsStay =
                options[bestOptionIndex]
                    .AdjustedScore -
                options[0]
                    .AdjustedScore;
        }

        return new LaneAdvisorCalculationResult(
            Generation:
                generation,

            LocalIndex:
                localIndex,

            HeroIds:
                heroIds,

            HeroNames:
                heroNames,

            ApiRows:
                matchupRows.Count,

            Options:
                options,

            BestOptionIndex:
                bestOptionIndex,

            ImprovementVsStay:
                improvementVsStay
        );
    }

    private static IReadOnlyList<string>
        BuildDiagnosticLines(
            LaneAdvisorCalculationResult result
        )
    {
        var lines =
            new List<string>
            {
                "LANE ADVISOR: RESULT" +
                $" | generation={result.Generation}" +
                $" | localIndex={result.LocalIndex}" +
                $" | hero={result.HeroNames[result.LocalIndex]}" +
                $"({result.HeroIds[result.LocalIndex]})" +
                $" | apiRows={result.ApiRows}"
            };

        foreach (
            var optionResult in
            result.Options
        )
        {
            var option =
                optionResult.Option;

            lines.Add(
                "LANE ADVISOR: OPTION" +
                $" | {optionResult.Label}" +
                $" | targetPair={option.TargetPair}" +
                $" | local=[{result.LocalIndex}] " +
                result.HeroNames[result.LocalIndex] +
                $"({result.HeroIds[result.LocalIndex]})" +
                $" | teammate=[{option.TeammateIndex}] " +
                result.HeroNames[
                    option.TeammateIndex
                ] +
                $"({result.HeroIds[option.TeammateIndex]})" +
                $" | enemies=[{option.EnemyIndex1}] " +
                result.HeroNames[
                    option.EnemyIndex1
                ] +
                $"({result.HeroIds[option.EnemyIndex1]})" +
                $" + [{option.EnemyIndex2}] " +
                result.HeroNames[
                    option.EnemyIndex2
                ] +
                $"({result.HeroIds[option.EnemyIndex2]})"
            );

            if (!optionResult.HasMatchData)
            {
                lines.Add(
                    "LANE ADVISOR: DATA" +
                    $" | {optionResult.Label}" +
                    " | no-data" +
                    " | confidenceLevel=INSUFFICIENT" +
                    " | eligible=false"
                );

                continue;
            }

            var winRatePercent =
                100.0 *
                optionResult.Wins /
                optionResult.Matches;

            if (!optionResult.HasNetWorthData)
            {
                lines.Add(
                    "LANE ADVISOR: DATA" +
                    $" | {optionResult.Label}" +
                    $" | laneRows={optionResult.LaneRows}" +
                    $" | matches={optionResult.Matches}" +
                    $" | wins={optionResult.Wins}" +
                    $" | winrate=" +
                    winRatePercent
                        .ToString(
                            "0.00",
                            CultureInfo.InvariantCulture
                        ) +
                    "%" +
                    " | netWorthDiff15=no-data" +
                    " | netWorthMatches=0" +
                    " | confidenceWeight=0.00" +
                    " | confidenceLevel=INSUFFICIENT" +
                    " | adjustedScore=no-data" +
                    " | eligible=false"
                );

                continue;
            }

            lines.Add(
                "LANE ADVISOR: DATA" +
                $" | {optionResult.Label}" +
                $" | laneRows={optionResult.LaneRows}" +
                $" | matches={optionResult.Matches}" +
                $" | wins={optionResult.Wins}" +
                $" | winrate=" +
                winRatePercent
                    .ToString(
                        "0.00",
                        CultureInfo.InvariantCulture
                    ) +
                "%" +
                $" | netWorthDiff15=" +
                optionResult.NetWorthDiff15
                    .ToString(
                        "+0.##;-0.##;0",
                        CultureInfo.InvariantCulture
                    ) +
                $" | netWorthMatches={optionResult.NetWorthMatches}" +
                $" | confidenceWeight=" +
                optionResult.ConfidenceWeight
                    .ToString(
                        "0.00",
                        CultureInfo.InvariantCulture
                    ) +
                $" | confidenceLevel={optionResult.ConfidenceLevel}" +
                $" | adjustedScore=" +
                optionResult.AdjustedScore
                    .ToString(
                        "+0.##;-0.##;0",
                        CultureInfo.InvariantCulture
                    ) +
                $" | eligible=" +
                (
                    optionResult.Eligible
                        ? "true"
                        : "false"
                )
            );
        }

        if (
            result.BestOptionIndex <
                0
        )
        {
            lines.Add(
                "LANE ADVISOR: BEST" +
                " | insufficient-data" +
                $" | minimumNetWorthMatches=" +
                    MinimumRecommendationMatches
            );

            return lines;
        }

        var best =
            result.Options[
                result.BestOptionIndex
            ];

        var improvementText =
            result.ImprovementVsStay.HasValue
                ? result
                    .ImprovementVsStay
                    .Value
                    .ToString(
                        "+0.##;-0.##;0",
                        CultureInfo.InvariantCulture
                    )
                : "unreliable";

        lines.Add(
            "LANE ADVISOR: BEST" +
            $" | {best.Label}" +
            $" | adjustedScore=" +
            best.AdjustedScore
                .ToString(
                    "+0.##;-0.##;0",
                    CultureInfo.InvariantCulture
                ) +
            $" | improvementVsStay={improvementText}" +
            $" | netWorthDiff15=" +
            best.NetWorthDiff15
                .ToString(
                    "+0.##;-0.##;0",
                    CultureInfo.InvariantCulture
                ) +
            $" | netWorthMatches={best.NetWorthMatches}" +
            $" | confidenceWeight=" +
            best.ConfidenceWeight
                .ToString(
                    "0.00",
                    CultureInfo.InvariantCulture
                ) +
            $" | confidenceLevel={best.ConfidenceLevel}"
        );

        return lines;
    }

    /*
     * READY payload:
     *
     * byte status
     * byte localIndex
     * byte optionCount
     *
     * option:
     *
     * byte swapWithIndex
     * byte flags
     *
     * bit0 = has match data
     * bit1 = has net worth data
     * bit2 = BEST
     *
     * uint16 WR * 100
     * int32 S15 * 100
     * uint32 WR matches
     * uint32 S15 sample_matches
     */
    private static byte[] BuildReadyPacket(
        LaneAdvisorCalculationResult result
    )
    {
        if (
            result.Options.Count !=
                ExpectedOptions
        )
        {
            throw new InvalidOperationException(
                "Invalid number of Lane Advisor " +
                "options for the transport packet."
            );
        }

        var payload =
            new byte[
                3 +
                result.Options.Count *
                    TransportOptionBytes
            ];

        payload[0] =
            TransportStatusReady;

        payload[1] =
            checked(
                (byte)result.LocalIndex
            );

        payload[2] =
            checked(
                (byte)result.Options.Count
            );

        for (
            var index = 0;
            index < result.Options.Count;
            index++
        )
        {
            var option =
                result.Options[index];

            var offset =
                3 +
                index *
                    TransportOptionBytes;

            payload[offset] =
                option.Option
                    .SwapWithIndex
                    .HasValue
                    ? checked(
                        (byte)option.Option
                            .SwapWithIndex
                            .Value
                    )
                    : TransportSwapStay;

            byte flags =
                0;

            if (option.HasMatchData)
            {
                flags |=
                    TransportFlagHasMatchData;
            }

            if (option.HasNetWorthData)
            {
                flags |=
                    TransportFlagHasNetWorthData;
            }

            if (
                index ==
                    result.BestOptionIndex
            )
            {
                flags |=
                    TransportFlagIsBest;
            }

            payload[offset + 1] =
                flags;

            var winRatePercent =
                option.HasMatchData
                    ? 100.0 *
                        option.Wins /
                        option.Matches
                    : 0;

            BinaryPrimitives
                .WriteUInt16LittleEndian(
                    payload.AsSpan(
                        offset + 2,
                        2
                    ),
                    ScaleWinRatePercent(
                        winRatePercent
                    )
                );

            BinaryPrimitives
                .WriteInt32LittleEndian(
                    payload.AsSpan(
                        offset + 4,
                        4
                    ),
                    option.HasNetWorthData
                        ? ScaleNetWorthDiff(
                            option.NetWorthDiff15
                        )
                        : 0
                );

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 8,
                        4
                    ),
                    ToTransportCount(
                        option.Matches,
                        "matches"
                    )
                );

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 12,
                        4
                    ),
                    ToTransportCount(
                        option.NetWorthMatches,
                        "netWorthMatches"
                    )
                );
        }

        return BridgeProtocol.CreatePacket(
            BridgeMessageType.LaneAdvisorRosterAck,
            payload
        );
    }

    private static byte[] BuildPendingPacket()
    {
        return BridgeProtocol.CreatePacket(
            BridgeMessageType.LaneAdvisorRosterAck,
            new byte[]
            {
                TransportStatusPending
            }
        );
    }

    private static byte[] BuildFailurePacket()
    {
        return BridgeProtocol.CreatePacket(
            BridgeMessageType.LaneAdvisorRosterAck,
            new byte[]
            {
                TransportStatusFailed
            }
        );
    }

    private static ushort ScaleWinRatePercent(
        double value
    )
    {
        var scaled =
            Math.Round(
                value *
                    100.0,
                MidpointRounding.AwayFromZero
            );

        if (
            !double.IsFinite(
                scaled
            ) ||
            scaled < 0 ||
            scaled > 10000
        )
        {
            throw new InvalidOperationException(
                "Lane Advisor winrate does not fit " +
                "in uint16 transport."
            );
        }

        return (ushort)scaled;
    }

    private static int ScaleNetWorthDiff(
        double value
    )
    {
        var scaled =
            Math.Round(
                value *
                    100.0,
                MidpointRounding.AwayFromZero
            );

        if (
            !double.IsFinite(
                scaled
            ) ||
            scaled <
                int.MinValue ||
            scaled >
                int.MaxValue
        )
        {
            throw new InvalidOperationException(
                "Lane Advisor netWorthDiff15 " +
                "does not fit in int32 transport."
            );
        }

        return (int)scaled;
    }

    private static uint ToTransportCount(
        ulong value,
        string fieldName
    )
    {
        if (
            value >
                uint.MaxValue
        )
        {
            throw new InvalidOperationException(
                "Lane Advisor " +
                fieldName +
                " does not fit in uint32 transport."
            );
        }

        return (uint)value;
    }

    private static IReadOnlyList<
        LaneAdvisorOption
    > BuildOptions(
        int localIndex
    )
    {
        var currentPair =
            localIndex /
            2;

        var currentEnemyIndex =
            TeamSize +
            currentPair *
                2;

        var result =
            new List<
                LaneAdvisorOption
            >(
                ExpectedOptions
            )
            {
                new(
                    TargetPair:
                        currentPair,

                    SwapWithIndex:
                        null,

                    TeammateIndex:
                        localIndex ^ 1,

                    EnemyIndex1:
                        currentEnemyIndex,

                    EnemyIndex2:
                        currentEnemyIndex + 1
                )
            };

        for (
            var allyIndex = 0;
            allyIndex < TeamSize;
            allyIndex++
        )
        {
            var targetPair =
                allyIndex /
                2;

            if (
                targetPair ==
                    currentPair
            )
            {
                continue;
            }

            var enemyIndex =
                TeamSize +
                targetPair *
                    2;

            result.Add(
                new LaneAdvisorOption(
                    TargetPair:
                        targetPair,

                    SwapWithIndex:
                        allyIndex,

                    TeammateIndex:
                        allyIndex ^ 1,

                    EnemyIndex1:
                        enemyIndex,

                    EnemyIndex2:
                        enemyIndex + 1
                )
            );
        }

        return result;
    }

    private static bool PairMatches(
        IReadOnlyList<uint>? pair,
        uint first,
        uint second
    )
    {
        if (
            pair is null ||
            pair.Count !=
                2
        )
        {
            return false;
        }

        var low =
            Math.Min(
                first,
                second
            );

        var high =
            Math.Max(
                first,
                second
            );

        return
            pair[0] == low &&
            pair[1] == high;
    }

    private static string BuildFingerprint(
        DeadlockLaneAdvisorRosterRequest
            roster
    )
    {
        var builder =
            new StringBuilder();

        builder.Append(
            roster.LocalIndex
        );

        builder.Append(
            ':'
        );

        for (
            var index = 0;
            index < roster.HeroNames.Count;
            index++
        )
        {
            if (index > 0)
            {
                builder.Append(
                    '|'
                );
            }

            builder.Append(
                DeadlockHeroCatalogService
                    .NormalizeName(
                        roster.HeroNames[
                            index
                        ]
                    )
            );
        }

        return builder.ToString();
    }

    private void Publish(
        long generation,
        string message
    )
    {
        Publish(
            generation,
            new[]
            {
                message
            }
        );
    }

    private void Publish(
        long generation,
        IReadOnlyList<string> messages
    )
    {
        lock (_stateGate)
        {
            if (
                _disposed ||
                generation !=
                    _generation
            )
            {
                return;
            }

            foreach (
                var message in messages
            )
            {
                _events.Enqueue(
                    new DeadlockLaneAdvisorDiagnosticEvent(
                        Id:
                            ++_nextEventId,

                        CreatedAtUtc:
                            DateTimeOffset.UtcNow,

                        Message:
                            message
                    )
                );

                while (
                    _events.Count >
                        MaximumDiagnosticEvents
                )
                {
                    _events.TryDequeue(
                        out _
                    );
                }
            }
        }
    }

    private void MarkRunFailed(
        LaneAdvisorRunState state
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

            state.Packet =
                null;

            state.Failed =
                true;
        }
    }

    private static void CancelRunState(
        LaneAdvisorRunState state
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

        LaneAdvisorRunState?
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

    private sealed class LaneAdvisorRunState
    {
        public LaneAdvisorRunState(
            long Generation,
            long RosterVersion,
            CancellationTokenSource Cancellation
        )
        {
            this.Generation =
                Generation;

            this.RosterVersion =
                RosterVersion;

            this.Cancellation =
                Cancellation;
        }

        public long Generation
        {
            get;
        }

        public long RosterVersion
        {
            get;
            set;
        }

        public CancellationTokenSource Cancellation
        {
            get;
        }

        public byte[]? Packet
        {
            get;
            set;
        }

        public bool Failed
        {
            get;
            set;
        }
    }

    private sealed record LaneAdvisorOption(
        int TargetPair,
        int? SwapWithIndex,
        int TeammateIndex,
        int EnemyIndex1,
        int EnemyIndex2
    );

    private sealed record LaneAdvisorOptionResult(
        LaneAdvisorOption Option,
        string Label,
        int LaneRows,
        bool HasMatchData,
        ulong Matches,
        ulong Wins,
        bool HasNetWorthData,
        double NetWorthDiff15,
        ulong NetWorthMatches,
        double ConfidenceWeight,
        string ConfidenceLevel,
        bool Eligible,
        double AdjustedScore
    );

    private sealed record LaneAdvisorCalculationResult(
        long Generation,
        int LocalIndex,
        IReadOnlyList<uint> HeroIds,
        IReadOnlyList<string> HeroNames,
        int ApiRows,
        IReadOnlyList<LaneAdvisorOptionResult> Options,
        int BestOptionIndex,
        double? ImprovementVsStay
    );
}

internal enum DeadlockLaneAdvisorResultState
{
    Pending,
    Ready,
    Failed
}

internal sealed record
    DeadlockLaneAdvisorRosterRequest(
        long RosterVersion,
        int LocalIndex,
        IReadOnlyList<string> HeroNames
    );

internal static class
    DeadlockLaneAdvisorRosterQueryParser
{
    private const int MaximumHeroNameLength =
        100;

    public static bool TryParse(
        IQueryCollection query,
        out DeadlockLaneAdvisorRosterRequest?
            request,
        out string error
    )
    {
        request =
            null;

        error =
            String.Empty;

        if (
            !int.TryParse(
                query["count"],
                out var count
            ) ||
            count !=
                DeadlockLaneAdvisorService
                    .ExpectedPlayers
        )
        {
            error =
                "Lane Advisor count must be equal to " +
                DeadlockLaneAdvisorService
                    .ExpectedPlayers +
                ".";

            return false;
        }

        if (
            !long.TryParse(
                query["version"],
                out var rosterVersion
            ) ||
            rosterVersion <=
                0
        )
        {
            error =
                "Invalid Lane Advisor version.";

            return false;
        }

        if (
            !int.TryParse(
                query["localIndex"],
                out var localIndex
            ) ||
            localIndex < 0 ||
            localIndex >=
                6
        )
        {
            error =
                "Lane Advisor localIndex must be " +
                "from 0 to 5.";

            return false;
        }

        var heroNames =
            new string[
                DeadlockLaneAdvisorService
                    .ExpectedPlayers
            ];

        for (
            var index = 0;
            index < heroNames.Length;
            index++
        )
        {
            var parameter =
                "h" +
                index;

            var heroName =
                query[parameter]
                    .ToString()
                    .Trim();

            if (
                String.IsNullOrWhiteSpace(
                    heroName
                )
            )
            {
                error =
                    "Parameter " +
                    parameter +
                    " is empty.";

                return false;
            }

            if (
                heroName.Length >
                    MaximumHeroNameLength
            )
            {
                error =
                    "Parameter " +
                    parameter +
                    " is too long.";

                return false;
            }

            heroNames[index] =
                heroName;
        }

        request =
            new DeadlockLaneAdvisorRosterRequest(
                RosterVersion:
                    rosterVersion,

                LocalIndex:
                    localIndex,

                HeroNames:
                    heroNames
            );

        return true;
    }
}

internal sealed record
    DeadlockLaneAdvisorDiagnosticEvent(
        long Id,
        DateTimeOffset CreatedAtUtc,
        string Message
    );
