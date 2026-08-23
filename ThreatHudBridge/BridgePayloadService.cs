using System.Buffers.Binary;
using System.Text;
using Steamworks;

internal sealed class BridgePayloadService
{
    private const string CurrentMatchChannel =
        "current-match";

    private const string
        CurrentMatchIdentitiesChannel =
            "current-match-identities";

    private const int ExpectedMatchPlayers =
        12;

    private const int MaximumPersonaNameBytes =
        byte.MaxValue;

    private static readonly Encoding Utf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier:
                false,

            throwOnInvalidBytes:
                true
        );

    private readonly object _steamGate;
    private readonly CSteamID _ownSteamId;
    private readonly uint _deadlockAppId;
    private readonly int _maximumCoplayPlayers;
    private readonly Action<string> _log;

    public BridgePayloadService(
        object steamGate,
        CSteamID ownSteamId,
        uint deadlockAppId,
        int maximumCoplayPlayers,
        Action<string>? log = null
    )
    {
        _steamGate =
            steamGate;

        _ownSteamId =
            ownSteamId;

        _deadlockAppId =
            deadlockAppId;

        _maximumCoplayPlayers =
            maximumCoplayPlayers;

        _log =
            log ??
            (_ => { });
    }

    public IReadOnlyList<CurrentMatchPlayer>
        GetCurrentMatchPlayers()
    {
        return ReadLatestMatchPlayers();
    }

    public byte[] BuildPacket(
        string channel
    )
    {
        return channel switch
        {
            CurrentMatchChannel =>
                BuildCurrentMatchPacket(),

            CurrentMatchIdentitiesChannel =>
                BuildCurrentMatchIdentitiesPacket(),

            _ =>
                throw new InvalidOperationException(
                    "Unknown transport channel: " +
                    channel
                )
        };
    }

    public object BuildDiagnosticSnapshot()
    {
        var players =
            ReadLatestMatchPlayers();

        return new
        {
            ok = true,

            generatedAtUtc =
                DateTimeOffset.UtcNow
                    .ToString("O"),

            ownSteamId64 =
                _ownSteamId
                    .m_SteamID
                    .ToString(),

            ownAccountId =
                ToAccountId(
                    _ownSteamId
                ),

            players =
                players.Select(
                    player =>
                        new
                        {
                            steamId64 =
                                player
                                    .SteamId64
                                    .ToString(),

                            accountId =
                                player.AccountId,

                            player.PersonaName,
                            player.CoplayUnixTime,
                            player.IsLocal,

                            personaNameUtf8Bytes =
                                Utf8.GetByteCount(
                                    player.PersonaName
                                )
                        }
                )
        };
    }

    private byte[] BuildCurrentMatchPacket()
    {
        var players =
            ReadLatestMatchPlayers();

        ValidatePlayerCount(
            players
        );

        var payload =
            new byte[
                1 +
                players.Count * 5
            ];

        payload[0] =
            (byte)players.Count;

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            var offset =
                1 +
                index * 5;

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset,
                        4
                    ),

                    player.AccountId
                );

            payload[offset + 4] =
                BuildPlayerFlags(
                    player
                );
        }

        _log(
            "Payload current-match: " +
            $"players={players.Count}, " +
            $"payloadBytes={payload.Length}"
        );

        return BridgeProtocol.CreatePacket(
            BridgeMessageType
                .CurrentMatchPlayers,

            payload
        );
    }

    private byte[]
        BuildCurrentMatchIdentitiesPacket()
    {
        var players =
            ReadLatestMatchPlayers();

        ValidatePlayerCount(
            players
        );

        var encodedNames =
            new byte[
                players.Count
            ][];

        var payloadLength =
            1;

        var totalNameBytes =
            0;

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            var personaName =
                player.PersonaName ??
                string.Empty;

            var nameBytes =
                Utf8.GetBytes(
                    personaName
                );

            if (
                nameBytes.Length >
                MaximumPersonaNameBytes
            )
            {
                throw new InvalidOperationException(
                    "PersonaName does not fit in the packet" +
                    $" | accountID={player.AccountId}" +
                    $" | utf8Bytes={nameBytes.Length}" +
                    $" | maximum={MaximumPersonaNameBytes}"
                );
            }

            encodedNames[index] =
                nameBytes;

            payloadLength =
                checked(
                    payloadLength +
                    6 +
                    nameBytes.Length
                );

            totalNameBytes =
                checked(
                    totalNameBytes +
                    nameBytes.Length
                );
        }

        var payload =
            new byte[
                payloadLength
            ];

        payload[0] =
            (byte)players.Count;

        var offset =
            1;

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            var nameBytes =
                encodedNames[index];

            BinaryPrimitives
                .WriteUInt32LittleEndian(
                    payload.AsSpan(
                        offset,
                        4
                    ),

                    player.AccountId
                );

            offset +=
                4;

            payload[offset] =
                BuildPlayerFlags(
                    player
                );

            offset +=
                1;

            payload[offset] =
                checked(
                    (byte)nameBytes.Length
                );

            offset +=
                1;

            nameBytes
                .AsSpan()
                .CopyTo(
                    payload.AsSpan(
                        offset,
                        nameBytes.Length
                    )
                );

            offset +=
                nameBytes.Length;
        }

        if (
            offset !=
            payload.Length
        )
        {
            throw new InvalidOperationException(
                "Error building " +
                "current-match-identities payload" +
                $" | expected={payload.Length}" +
                $" | actual={offset}"
            );
        }

        _log(
            "Payload current-match-identities: " +
            $"players={players.Count}, " +
            $"nameBytes={totalNameBytes}, " +
            $"payloadBytes={payload.Length}"
        );

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            _log(
                "  Identity " +
                $"[{index}] " +
                $"accountID={player.AccountId} " +
                $"local={player.IsLocal} " +
                $"nameBytes={encodedNames[index].Length} " +
                $"name=[{player.PersonaName}]"
            );
        }

        return BridgeProtocol.CreatePacket(
            BridgeMessageType
                .CurrentMatchPlayerIdentities,

            payload
        );
    }

    private static byte BuildPlayerFlags(
        CurrentMatchPlayer player
    )
    {
        byte flags =
            0;

        if (player.IsLocal)
        {
            flags |=
                1;
        }

        return flags;
    }

    private static void ValidatePlayerCount(
        IReadOnlyList<CurrentMatchPlayer>
            players
    )
    {
        if (
            players.Count >
            byte.MaxValue
        )
        {
            throw new InvalidOperationException(
                "Player count does not fit " +
                "in the transport packet."
            );
        }
    }

    private IReadOnlyList<CurrentMatchPlayer>
        ReadLatestMatchPlayers()
    {
        var candidates =
            new List<CurrentMatchPlayer>();

        int rawCount;

        lock (_steamGate)
        {
            rawCount =
                SteamFriends
                    .GetCoplayFriendCount();

            var safeCount =
                Math.Clamp(
                    rawCount,
                    0,
                    _maximumCoplayPlayers
                );

            var seenSteamIds =
                new HashSet<ulong>();

            for (
                var index = 0;
                index < safeCount;
                index++
            )
            {
                var steamId =
                    SteamFriends
                        .GetCoplayFriend(
                            index
                        );

                if (
                    !steamId.IsValid() ||
                    !seenSteamIds.Add(
                        steamId.m_SteamID
                    )
                )
                {
                    continue;
                }

                var appId =
                    SteamFriends
                        .GetFriendCoplayGame(
                            steamId
                        )
                        .m_AppId;

                if (
                    appId !=
                    _deadlockAppId
                )
                {
                    continue;
                }

                SteamFriends
                    .RequestUserInformation(
                        steamId,
                        true
                    );

                candidates.Add(
                    new CurrentMatchPlayer(
                        SteamId64:
                            steamId.m_SteamID,

                        AccountId:
                            ToAccountId(
                                steamId
                            ),

                        PersonaName:
                            SteamFriends
                                .GetFriendPersonaName(
                                    steamId
                                ) ??
                            string.Empty,

                        CoplayUnixTime:
                            Convert.ToInt64(
                                SteamFriends
                                    .GetFriendCoplayTime(
                                        steamId
                                    )
                            ),

                        IsLocal:
                            steamId.m_SteamID ==
                            _ownSteamId.m_SteamID
                    )
                );
            }
        }

        candidates.Sort(
            static (
                left,
                right
            ) =>
                right
                    .CoplayUnixTime
                    .CompareTo(
                        left.CoplayUnixTime
                    )
        );

        var latestGroup =
            candidates
                .Take(
                    ExpectedMatchPlayers
                )
                .ToList();

        var localCount =
            latestGroup.Count(
                player =>
                    player.IsLocal
            );

        _log(
            "Steam recent snapshot: " +
            $"raw={rawCount}, " +
            $"deadlock={candidates.Count}, " +
            $"selected={latestGroup.Count}, " +
            $"localCount={localCount}"
        );

        for (
            var index = 0;
            index < latestGroup.Count;
            index++
        )
        {
            var player =
                latestGroup[index];

            _log(
                $"  [{index}] " +
                $"accountID={player.AccountId} " +
                $"local={player.IsLocal} " +
                $"name=[{player.PersonaName}]"
            );
        }

        if (
            latestGroup.Count !=
            ExpectedMatchPlayers
        )
        {
            _log(
                "WARNING: expected " +
                $"{ExpectedMatchPlayers} players, " +
                $"received {latestGroup.Count}."
            );
        }

        if (localCount != 1)
        {
            _log(
                "WARNING: expected one local " +
                $"player, found {localCount}."
            );
        }

        return latestGroup;
    }

    private static uint ToAccountId(
        CSteamID steamId
    )
    {
        return unchecked(
            (uint)(
                steamId.m_SteamID &
                0xFFFFFFFFUL
            )
        );
    }
}

internal sealed record CurrentMatchPlayer(
    ulong SteamId64,
    uint AccountId,
    string PersonaName,
    long CoplayUnixTime,
    bool IsLocal
);

internal enum BridgeMessageType : byte
{
    CurrentMatchPlayers =
        1,

    PlayerStats =
        2,

    CurrentMatchPlayerIdentities =
        3,

    PlayerRanks =
        4,

    PlayerHeroReactionAck =
        5,

    LaneAdvisorRosterAck =
        6,

    CurrentMatchHeroDamage =
        7,

    ServiceStatusAck =
        8
}

internal static class BridgeProtocol
{
    public const byte Version =
        1;

    public const int HeaderSize =
        8;

    private const byte Magic0 =
        0x54;

    private const byte Magic1 =
        0x48;

    public static byte[] CreatePacket(
        BridgeMessageType messageType,
        ReadOnlySpan<byte> payload
    )
    {
        if (
            payload.Length >
            ushort.MaxValue
        )
        {
            throw new InvalidOperationException(
                "Transport payload exceeds " +
                "65535 bytes."
            );
        }

        var packet =
            new byte[
                HeaderSize +
                payload.Length
            ];

        packet[0] =
            Magic0;

        packet[1] =
            Magic1;

        packet[2] =
            Version;

        packet[3] =
            (byte)messageType;

        BinaryPrimitives
            .WriteUInt16LittleEndian(
                packet.AsSpan(
                    4,
                    2
                ),

                (ushort)payload.Length
            );

        BinaryPrimitives
            .WriteUInt16LittleEndian(
                packet.AsSpan(
                    6,
                    2
                ),

                ComputeCrc16(
                    payload
                )
            );

        payload.CopyTo(
            packet.AsSpan(
                HeaderSize
            )
        );

        return packet;
    }

    private static ushort ComputeCrc16(
        ReadOnlySpan<byte> bytes
    )
    {
        ushort crc =
            0xFFFF;

        foreach (
            var value in bytes
        )
        {
            crc ^=
                (ushort)(
                    value << 8
                );

            for (
                var bit = 0;
                bit < 8;
                bit++
            )
            {
                crc =
                    (
                        crc &
                        0x8000
                    ) != 0

                        ? (ushort)(
                            (
                                crc << 1
                            ) ^
                            0x1021
                        )

                        : (ushort)(
                            crc << 1
                        );
            }
        }

        return crc;
    }
}
