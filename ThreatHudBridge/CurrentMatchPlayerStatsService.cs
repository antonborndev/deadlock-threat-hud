using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;

internal sealed class CurrentMatchPlayerStatsService
{
    public const string Channel =
        "player-stats";

    public const int ExpectedPlayers =
        12;

    private const int BytesPerPlayer =
        14;

    private readonly DeadlockHeroCatalogService
        _heroCatalogService;

    private readonly DeadlockPlayerStatsService
        _playerStatsService;

    private readonly PlayerReactionStore
        _reactionStore;

    private readonly Action<string>
        _log;

    public CurrentMatchPlayerStatsService(
        DeadlockHeroCatalogService heroCatalogService,
        DeadlockPlayerStatsService playerStatsService,
        PlayerReactionStore reactionStore,
        Action<string>? log = null
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

        _reactionStore =
            reactionStore ??
            throw new ArgumentNullException(
                nameof(reactionStore)
            );

        _log =
            log ??
            (_ => { });
    }

    public async Task<
        CurrentMatchPlayerStatsSnapshot
    > GetSnapshotAsync(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests,
        CancellationToken cancellationToken
    )
    {
        ValidateRequests(
            requests
        );

        /*
         * The reaction applies only to accountID.
         *
         * It is read independently of the
         * hero resolution result and therefore remains
         * available even when hero-unknown or
         * stats-not-found.
         */
        var reactionsByAccountId =
            await _reactionStore.GetManyAsync(
                requests.Select(
                    request =>
                        request.AccountId
                ),
                cancellationToken
            );

        var resolutions =
            await _heroCatalogService.ResolveAsync(
                requests.Select(
                    request =>
                        request.HeroName
                ),
                cancellationToken
            );

        if (
            resolutions.Count !=
                requests.Count
        )
        {
            throw new InvalidOperationException(
                "Hero catalog returned an invalid " +
                "number of results."
            );
        }

        var resolvedHeroIds =
            resolutions
                .Where(
                    resolution =>
                        resolution.Status ==
                            "resolved" &&
                        resolution.HeroId.HasValue
                )
                .Select(
                    resolution =>
                        resolution.HeroId!.Value
                )
                .Distinct()
                .ToArray();

        IReadOnlyList<DeadlockHeroStats>
            stats;

        if (
            resolvedHeroIds.Length == 0
        )
        {
            stats =
                Array.Empty<DeadlockHeroStats>();
        }
        else
        {
            stats =
                await _playerStatsService
                    .GetHeroStatsAsync(
                        requests.Select(
                            request =>
                                request.AccountId
                        ),
                        resolvedHeroIds,
                        cancellationToken
                    );
        }

        /*
         * Deadlock API receives separately:
         *
         * account_ids = requested players
         * hero_ids    = current heroes
         *
         * Therefore the API may return additional
         * combinations. Keep only the exact pair
         * accountID + heroID for each request.
         */
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
                CurrentMatchPlayerStatsEntry
            >(
                requests.Count
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

            var reaction =
                reactionsByAccountId[
                    request.AccountId
                ];

            CurrentMatchPlayerStatsEntry
                entry;

            if (
                resolution.Status ==
                    "unknown" ||
                !resolution.HeroId.HasValue
            )
            {
                entry =
                    CreateEntry(
                        request,
                        apiHeroName:
                            null,
                        status:
                            CurrentMatchPlayerStatsStatus
                                .HeroUnknown,
                        heroId:
                            0,
                        matchesPlayed:
                            0,
                        wins:
                            0,
                        reaction:
                            reaction
                    );
            }
            else if (
                resolution.Status ==
                    "ambiguous"
            )
            {
                entry =
                    CreateEntry(
                        request,
                        apiHeroName:
                            null,
                        status:
                            CurrentMatchPlayerStatsStatus
                                .HeroAmbiguous,
                        heroId:
                            0,
                        matchesPlayed:
                            0,
                        wins:
                            0,
                        reaction:
                            reaction
                    );
            }
            else
            {
                var heroId =
                    resolution.HeroId.Value;

                if (
                    !statsByPair.TryGetValue(
                        (
                            request.AccountId,
                            heroId
                        ),
                        out var heroStats
                    )
                )
                {
                    entry =
                        CreateEntry(
                            request,
                            resolution.ApiName,
                            CurrentMatchPlayerStatsStatus
                                .StatsNotFound,
                            heroId,
                            matchesPlayed:
                                0,
                            wins:
                                0,
                            reaction:
                                reaction
                        );
                }
                else
                {
                    entry =
                        CreateEntry(
                            request,
                            resolution.ApiName,
                            CurrentMatchPlayerStatsStatus.Ok,
                            heroId,
                            ToUInt32(
                                heroStats.MatchesPlayed,
                                "matchesPlayed",
                                request
                            ),
                            ToUInt32(
                                heroStats.Wins,
                                "wins",
                                request
                            ),
                            reaction
                        );
                }
            }

            players.Add(
                entry
            );
        }

        var snapshot =
            new CurrentMatchPlayerStatsSnapshot(
                GeneratedAtUtc:
                    DateTimeOffset.UtcNow,
                Players:
                    players
            );

        foreach (
            var player in players
        )
        {
            _log(
                "Deadlock API" +
                $" [{player.Index}]" +
                $" | accountID={player.AccountId}" +
                $" | apiHero={player.ApiHeroName ?? "-"}" +
                $" | heroID={player.HeroId}" +
                $" | status={player.Status}" +
                $" | matches={player.MatchesPlayed}" +
                $" | wins={player.Wins}" +
                $" | winrate={player.WinRatePercent:F2}%" +
                $" | reaction={player.Reaction}"
            );
        }

        return snapshot;
    }

    public async Task<byte[]> BuildPacketAsync(
        IReadOnlyList<
            CurrentMatchPlayerHeroRequest
        > requests,
        CancellationToken cancellationToken
    )
    {
        var snapshot =
            await GetSnapshotAsync(
                requests,
                cancellationToken
            );

        if (
            snapshot.Players.Count >
                byte.MaxValue
        )
        {
            throw new InvalidOperationException(
                "The number of player-stats records " +
                "does not fit in the packet."
            );
        }

        /*
         * Payload:
         *
         * byte playerCount
         *
         * Then N records of 14 bytes each:
         *
         * byte status
         * uint32 LE heroID
         * uint32 LE matchesPlayed
         * uint32 LE wins
         * byte player reaction
         *
         * reaction:
         *   0   = none
         *   1   = like
         *   255 = dislike (-1)
         */
        var payload =
            new byte[
                1 +
                snapshot.Players.Count *
                    BytesPerPlayer
            ];

        payload[0] =
            (byte)snapshot.Players.Count;

        for (
            var index = 0;
            index < snapshot.Players.Count;
            index++
        )
        {
            var player =
                snapshot.Players[index];

            var offset =
                1 +
                index *
                    BytesPerPlayer;

            payload[offset] =
                (byte)player.Status;

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 1,
                        4
                    ),
                    player.HeroId
                );

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 5,
                        4
                    ),
                    player.MatchesPlayed
                );

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 9,
                        4
                    ),
                    player.Wins
                );

            payload[offset + 13] =
                PlayerReactionValue
                    .EncodeTransportByte(
                        player.Reaction
                    );
        }

        return BridgeProtocol.CreatePacket(
            BridgeMessageType.PlayerStats,
            payload
        );
    }

    private static CurrentMatchPlayerStatsEntry
        CreateEntry(
            CurrentMatchPlayerHeroRequest request,
            string? apiHeroName,
            CurrentMatchPlayerStatsStatus status,
            uint heroId,
            uint matchesPlayed,
            uint wins,
            int reaction
        )
    {
        return new CurrentMatchPlayerStatsEntry(
            Index:
                request.Index,
            AccountId:
                request.AccountId,
            InputHeroName:
                request.HeroName,
            ApiHeroName:
                apiHeroName,
            Status:
                status,
            HeroId:
                heroId,
            MatchesPlayed:
                matchesPlayed,
            Wins:
                wins,
            Reaction:
                reaction
        );
    }

    private static uint ToUInt32(
        ulong value,
        string fieldName,
        CurrentMatchPlayerHeroRequest request
    )
    {
        if (
            value >
                uint.MaxValue
        )
        {
            throw new InvalidOperationException(
                $"{fieldName} does not fit in uint32" +
                $" | index={request.Index}" +
                $" | accountID={request.AccountId}" +
                $" | value={value}"
            );
        }

        return (uint)value;
    }

    private static void ValidateRequests(
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
            throw new InvalidOperationException(
                "player-stats accepts " +
                $"from 1 to {ExpectedPlayers} players, " +
                $"received {requests.Count}."
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
                    "player-stats request order is invalid " +
                    $"at position {index}."
                );
            }

            if (
                request.AccountId == 0
            )
            {
                throw new InvalidOperationException(
                    "Zero accountID" +
                    $" | index={index}"
                );
            }

            if (
                !accountIds.Add(
                    request.AccountId
                )
            )
            {
                throw new InvalidOperationException(
                    "Duplicate accountID" +
                    $" | index={index}" +
                    $" | accountID={request.AccountId}"
                );
            }

            if (
                string.IsNullOrWhiteSpace(
                    request.HeroName
                )
            )
            {
                throw new InvalidOperationException(
                    "Empty hero name" +
                    $" | index={index}"
                );
            }
        }
    }
}

internal static class CurrentMatchPlayerStatsQueryParser
{
    private const int MaximumHeroNameLength =
        100;

    public static bool TryParse(
        IQueryCollection query,
        out CurrentMatchPlayerHeroRequest[] requests,
        out string error
    )
    {
        requests =
            Array.Empty<
                CurrentMatchPlayerHeroRequest
            >();

        error =
            string.Empty;

        if (
            !int.TryParse(
                query["count"],
                out var count
            ) ||
            count < 1 ||
            count >
                CurrentMatchPlayerStatsService
                    .ExpectedPlayers
        )
        {
            error =
                "count parameter must be from 1 to " +
                CurrentMatchPlayerStatsService
                    .ExpectedPlayers +
                ".";

            return false;
        }

        var parsed =
            new CurrentMatchPlayerHeroRequest[
                count
            ];

        var accountIds =
            new HashSet<uint>();

        for (
            var index = 0;
            index < count;
            index++
        )
        {
            var accountParameter =
                "a" +
                index;

            var heroParameter =
                "h" +
                index;

            if (
                !uint.TryParse(
                    query[accountParameter],
                    out var accountId
                ) ||
                accountId == 0
            )
            {
                error =
                    "Invalid parameter " +
                    accountParameter +
                    ".";

                return false;
            }

            if (
                !accountIds.Add(
                    accountId
                )
            )
            {
                error =
                    "Duplicate accountID " +
                    accountId +
                    ".";

                return false;
            }

            var heroName =
                query[heroParameter]
                    .ToString()
                    .Trim();

            if (
                string.IsNullOrWhiteSpace(
                    heroName
                )
            )
            {
                error =
                    "Parameter " +
                    heroParameter +
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
                    heroParameter +
                    " is too long.";

                return false;
            }

            parsed[index] =
                new CurrentMatchPlayerHeroRequest(
                    Index:
                        index,
                    AccountId:
                        accountId,
                    HeroName:
                        heroName
                );
        }

        requests =
            parsed;

        return true;
    }
}

internal sealed record CurrentMatchPlayerHeroRequest(
    int Index,
    uint AccountId,
    string HeroName
);

internal sealed record CurrentMatchPlayerStatsSnapshot(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<
        CurrentMatchPlayerStatsEntry
    > Players
);

internal sealed record CurrentMatchPlayerStatsEntry(
    int Index,
    uint AccountId,
    string InputHeroName,
    string? ApiHeroName,
    CurrentMatchPlayerStatsStatus Status,
    uint HeroId,
    uint MatchesPlayed,
    uint Wins,
    int Reaction
)
{
    public double WinRatePercent =>
        MatchesPlayed == 0
            ? 0
            : 100.0 *
                Wins /
                MatchesPlayed;
}

internal enum CurrentMatchPlayerStatsStatus : byte
{
    Ok =
        0,

    HeroUnknown =
        1,

    HeroAmbiguous =
        2,

    StatsNotFound =
        3
}