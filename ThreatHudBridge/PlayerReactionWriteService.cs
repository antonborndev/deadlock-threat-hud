using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;

internal readonly record struct PlayerReactionWriteRequest(
    uint AccountId,
    int Reaction
);

internal static class PlayerReactionWriteQueryParser
{
    public static bool TryParse(
        IQueryCollection query,
        out PlayerReactionWriteRequest request,
        out string error
    )
    {
        request =
            default;

        error =
            string.Empty;

        if (
            !uint.TryParse(
                query["accountId"],
                out var accountId
            ) ||
            accountId == 0
        )
        {
            error =
                "accountId parameter must be " +
                "a non-zero uint32.";

            return false;
        }

        if (
            !int.TryParse(
                query["reaction"],
                out var reaction
            ) ||
            !PlayerReactionValue.IsValid(
                reaction
            )
        )
        {
            error =
                "reaction parameter must be " +
                "-1, 0, or 1.";

            return false;
        }

        request =
            new PlayerReactionWriteRequest(
                accountId,
                reaction
            );

        return true;
    }
}

internal sealed class PlayerReactionWriteService
{
    public const string Channel =
        "player-reaction-set";

    private readonly PlayerReactionStore
        _reactionStore;

    private readonly Action<string>
        _log;

    public PlayerReactionWriteService(
        PlayerReactionStore reactionStore,
        Action<string>? log = null
    )
    {
        _reactionStore =
            reactionStore ??
            throw new ArgumentNullException(
                nameof(reactionStore)
            );

        _log =
            log ??
            (_ => { });
    }

    public async Task<byte[]> BuildPacketAsync(
        PlayerReactionWriteRequest request,
        CancellationToken cancellationToken
    )
    {
        await _reactionStore.SetAsync(
            request.AccountId,
            request.Reaction,
            cancellationToken
        );

        /*
         * ACK reports the actually stored
         * player-only state after the write.
         */
        var storedReaction =
            await _reactionStore.GetAsync(
                request.AccountId,
                cancellationToken
            );

        _log(
            "Player reaction WRITE: " +
            $"accountId={request.AccountId}, " +
            $"requested={request.Reaction}, " +
            $"stored={storedReaction}"
        );

        /*
         * ACK payload:
         *
         * uint32 LE accountId
         * byte reaction
         *
         * reaction:
         *   0   = none
         *   1   = like
         *   255 = dislike
         *
         * 5 bytes total.
         */
        var payload =
            new byte[5];

        BinaryPrimitives
            .WriteUInt32LittleEndian(
                payload.AsSpan(
                    0,
                    4
                ),
                request.AccountId
            );

        payload[4] =
            PlayerReactionValue
                .EncodeTransportByte(
                    storedReaction
                );

        /*
         * Numeric message type 5 is preserved.
         *
         * The old client expects payload=9 and
         * will explicitly reject the new payload=5, so
         * silent incorrect decoding cannot occur.
         */
        return BridgeProtocol.CreatePacket(
            BridgeMessageType.PlayerHeroReactionAck,
            payload
        );
    }
}