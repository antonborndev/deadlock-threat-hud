internal sealed class CurrentMatchPlayerRanksService
{
    public const string Channel =
        "player-ranks";

    public const int ExpectedPlayers =
        12;

    /*
     * For each player:
     *
     * byte status
     * byte rank
     * byte subrank
     */
    private const int BytesPerPlayer =
        3;

    private readonly DeadlockPlayerRankService
        _rankService;

    public CurrentMatchPlayerRanksService(
        DeadlockPlayerRankService rankService
    )
    {
        _rankService =
            rankService ??
            throw new ArgumentNullException(
                nameof(rankService)
            );
    }

    public async Task<
        CurrentMatchPlayerRanksSnapshot
    > GetSnapshotAsync(
        IReadOnlyList<
            CurrentMatchPlayerRankRequest
        > requests,

        CancellationToken cancellationToken
    )
    {
        ValidateRequests(
            requests
        );

        var rankResults =
            await _rankService.GetRanksAsync(
                requests
                    .Select(
                        request =>
                            request.AccountId
                    )
                    .ToArray(),

                cancellationToken
            );

        if (
            rankResults.Count !=
                requests.Count
        )
        {
            throw new InvalidOperationException(
                "Rank service returned an invalid " +
                "number of results."
            );
        }

        var players =
            new CurrentMatchPlayerRankEntry[
                requests.Count
            ];

        for (
            var index = 0;
            index < requests.Count;
            index++
        )
        {
            var request =
                requests[index];

            var rankResult =
                rankResults[index];

            if (
                rankResult.AccountId !=
                    request.AccountId
            )
            {
                throw new InvalidOperationException(
                    "Rank result order is invalid" +
                    $" | index={index}" +
                    $" | expected={request.AccountId}" +
                    $" | actual={rankResult.AccountId}"
                );
            }

            players[index] =
                new CurrentMatchPlayerRankEntry(
                    Index:
                        request.Index,

                    AccountId:
                        request.AccountId,

                    Status:
                        rankResult.Status,

                    Rank:
                        rankResult.Rank,

                    Subrank:
                        rankResult.Subrank
                );
        }

        return new CurrentMatchPlayerRanksSnapshot(
            GeneratedAtUtc:
                DateTimeOffset.UtcNow,

            Players:
                players
        );
    }

    public async Task<byte[]> BuildPacketAsync(
        IReadOnlyList<
            CurrentMatchPlayerRankRequest
        > requests,

        CancellationToken cancellationToken
    )
    {
        var result =
            await BuildPacketResultAsync(
                requests,
                cancellationToken
            );

        return result.Packet;
    }

    public async Task<
        CurrentMatchPlayerRanksPacketResult
    > BuildPacketResultAsync(
        IReadOnlyList<
            CurrentMatchPlayerRankRequest
        > requests,

        CancellationToken cancellationToken
    )
    {
        var snapshot =
            await GetSnapshotAsync(
                requests,
                cancellationToken
            );

        var payload =
            new byte[
                1 +
                snapshot.Players.Count *
                BytesPerPlayer
            ];

        payload[0] =
            checked(
                (byte)snapshot.Players.Count
            );

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

            payload[offset + 1] =
                player.Rank;

            payload[offset + 2] =
                player.Subrank;
        }

        var packet =
            BridgeProtocol.CreatePacket(
                BridgeMessageType.PlayerRanks,
                payload
            );

        return new CurrentMatchPlayerRanksPacketResult(
            Packet:
                packet,

            HasApiErrors:
                snapshot.Players.Any(
                    player =>
                        player.Status ==
                            DeadlockPlayerRankStatus
                                .ApiError
                )
        );
    }

    private static void ValidateRequests(
        IReadOnlyList<
            CurrentMatchPlayerRankRequest
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
                "player-ranks accepts " +
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
                request.Index != index
            )
            {
                throw new InvalidOperationException(
                    "player-ranks order is invalid" +
                    $" | index={index}" +
                    $" | requestIndex={request.Index}"
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
        }
    }
}

internal static class
    CurrentMatchPlayerRanksQueryParser
{
    public static bool TryParse(
        IQueryCollection query,
        out CurrentMatchPlayerRankRequest[]
            requests,
        out string error
    )
    {
        requests =
            Array.Empty<
                CurrentMatchPlayerRankRequest
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
                CurrentMatchPlayerRanksService
                    .ExpectedPlayers
        )
        {
            error =
                "count parameter must be from 1 to " +
                CurrentMatchPlayerRanksService
                    .ExpectedPlayers +
                ".";

            return false;
        }

        var parsed =
            new CurrentMatchPlayerRankRequest[
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
            var parameterName =
                "a" +
                index;

            if (
                !uint.TryParse(
                    query[
                        parameterName
                    ],

                    out var accountId
                ) ||
                accountId == 0
            )
            {
                error =
                    "Invalid parameter " +
                    parameterName +
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

            parsed[index] =
                new CurrentMatchPlayerRankRequest(
                    Index:
                        index,

                    AccountId:
                        accountId
                );
        }

        requests =
            parsed;

        return true;
    }
}

internal sealed record
    CurrentMatchPlayerRankRequest(
        int Index,
        uint AccountId
    );

internal sealed record
    CurrentMatchPlayerRanksSnapshot(
        DateTimeOffset GeneratedAtUtc,

        IReadOnlyList<
            CurrentMatchPlayerRankEntry
        > Players
    );

internal sealed record
    CurrentMatchPlayerRanksPacketResult(
        byte[] Packet,
        bool HasApiErrors
    );

internal sealed record
    CurrentMatchPlayerRankEntry(
        int Index,
        uint AccountId,
        DeadlockPlayerRankStatus Status,
        byte Rank,
        byte Subrank
    );
