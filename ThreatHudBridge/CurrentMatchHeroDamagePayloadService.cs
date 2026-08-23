using System.Buffers.Binary;
using System.Globalization;
using Microsoft.AspNetCore.Http;

internal static class CurrentMatchHeroDamagePayloadService
{
    public const string Channel =
        "current-match-hero-damage";

    private const int ExpectedPlayerCount =
        12;

    private const int PayloadHeaderSize =
        10;

    private const int BytesPerPlayer =
        5;

    private const int PayloadSize =
        PayloadHeaderSize +
        ExpectedPlayerCount * BytesPerPlayer;

    public static bool TryParseQuery(
        IQueryCollection query,
        out uint[] accountIds,
        out string error
    )
    {
        accountIds =
            Array.Empty<uint>();

        error =
            String.Empty;

        if (
            !Int32.TryParse(
                query["count"].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count
            ) ||
            count != ExpectedPlayerCount
        )
        {
            error =
                "Current-match hero-damage count must be exactly 12.";

            return false;
        }

        var parsedAccountIds =
            new uint[ExpectedPlayerCount];

        var seenAccountIds =
            new HashSet<uint>();

        for (
            var index = 0;
            index < ExpectedPlayerCount;
            index++
        )
        {
            var parameterName =
                "a" +
                index.ToString(
                    CultureInfo.InvariantCulture
                );

            if (
                !UInt32.TryParse(
                    query[parameterName].ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var accountId
                )
            )
            {
                error =
                    "Current-match hero-damage parameter " +
                    parameterName +
                    " must be an unsigned integer.";

                return false;
            }

            if (
                accountId != 0 &&
                !seenAccountIds.Add(
                    accountId
                )
            )
            {
                error =
                    "Current-match hero-damage account IDs must be unique" +
                    " | duplicate=" +
                    accountId.ToString(
                        CultureInfo.InvariantCulture
                    );

                return false;
            }

            parsedAccountIds[index] =
                accountId;
        }

        accountIds =
            parsedAccountIds;

        return true;
    }

    public static byte[] BuildPacket(
        IReadOnlyList<uint> accountIds,
        CurrentMatchLiveDamageSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(
            accountIds
        );

        ArgumentNullException.ThrowIfNull(
            snapshot
        );

        if (
            accountIds.Count !=
            ExpectedPlayerCount
        )
        {
            throw new InvalidOperationException(
                "Current-match hero-damage request must contain exactly 12 account IDs."
            );
        }

        var requestedAccountIds =
            new HashSet<uint>();

        for (
            var index = 0;
            index < accountIds.Count;
            index++
        )
        {
            var accountId =
                accountIds[index];

            if (
                accountId != 0 &&
                !requestedAccountIds.Add(
                    accountId
                )
            )
            {
                throw new InvalidOperationException(
                    "Current-match hero-damage request contains a duplicate account ID" +
                    " | accountId=" +
                    accountId.ToString(
                        CultureInfo.InvariantCulture
                    )
                );
            }
        }

        var players =
            snapshot.Players ??
            throw new InvalidOperationException(
                "Current-match hero-damage snapshot players are missing."
            );

        if (
            snapshot.MatchId == 0 &&
            players.Count != 0
        )
        {
            throw new InvalidOperationException(
                "Current-match hero-damage snapshot has players without a match ID."
            );
        }

        var damageByAccountId =
            new Dictionary<uint, uint>();

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            if (player.AccountId == 0)
            {
                throw new InvalidOperationException(
                    "Current-match hero-damage snapshot contains accountId=0."
                );
            }

            if (
                !damageByAccountId.TryAdd(
                    player.AccountId,
                    ToUInt32(
                        player.HeroDamage,
                        player.AccountId
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    "Current-match hero-damage snapshot contains a duplicate account ID" +
                    " | accountId=" +
                    player.AccountId.ToString(
                        CultureInfo.InvariantCulture
                    )
                );
            }
        }

        var payload =
            new byte[PayloadSize];

        payload[0] =
            snapshot.MatchId == 0
                ? (byte)0
                : players.Count == 0
                    ? (byte)1
                    : (byte)2;

        BinaryPrimitives
            .WriteUInt64LittleEndian(
                payload.AsSpan(
                    1,
                    8
                ),

                snapshot.MatchId
            );

        payload[9] =
            ExpectedPlayerCount;

        for (
            var index = 0;
            index < ExpectedPlayerCount;
            index++
        )
        {
            var offset =
                PayloadHeaderSize +
                index * BytesPerPlayer;

            var accountId =
                accountIds[index];

            if (
                accountId == 0 ||
                !damageByAccountId.TryGetValue(
                    accountId,
                    out var damage
                )
            )
            {
                continue;
            }

            payload[offset] =
                1;

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset + 1,
                        4
                    ),

                    damage
                );
        }

        return BridgeProtocol.CreatePacket(
            BridgeMessageType
                .CurrentMatchHeroDamage,

            payload
        );
    }

    private static uint ToUInt32(
        long damage,
        uint accountId
    )
    {
        if (
            damage < 0 ||
            damage > UInt32.MaxValue
        )
        {
            throw new InvalidOperationException(
                "Current-match hero damage does not fit in uint32" +
                " | accountId=" +
                accountId.ToString(
                    CultureInfo.InvariantCulture
                ) +
                " | damage=" +
                damage.ToString(
                    CultureInfo.InvariantCulture
                )
            );
        }

        return checked(
            (uint)damage
        );
    }
}
