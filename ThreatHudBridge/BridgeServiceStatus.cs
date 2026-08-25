using Microsoft.AspNetCore.Http;

internal enum BridgeServiceKind
{
    Winrate,
    Rank,
    Adviser,
    HeroDamage
}

internal enum BridgeServiceState
{
    InProgress,
    Completed,
    Error
}

internal readonly record struct
    BridgeServiceRequestToken(
        BridgeServiceKind Service,
        long SessionOrdinal,
        long Sequence
    );

internal sealed record BridgeServiceStatusSnapshot(
    BridgeServiceState Winrate,
    BridgeServiceState Rank,
    BridgeServiceState Adviser,
    BridgeServiceState HeroDamage
)
{
    public static BridgeServiceStatusSnapshot InProgress =>
        new(
            BridgeServiceState.InProgress,
            BridgeServiceState.InProgress,
            BridgeServiceState.InProgress,
            BridgeServiceState.InProgress
        );

    public BridgeServiceState GetState(
        BridgeServiceKind service
    )
    {
        return service switch
        {
            BridgeServiceKind.Winrate =>
                Winrate,

            BridgeServiceKind.Rank =>
                Rank,

            BridgeServiceKind.Adviser =>
                Adviser,

            BridgeServiceKind.HeroDamage =>
                HeroDamage,

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(service),
                    service,
                    "Unknown Bridge service."
                )
        };
    }
}

internal static class BridgeServiceStateText
{
    public static string ToWireValue(
        BridgeServiceState state
    )
    {
        return state switch
        {
            BridgeServiceState.InProgress =>
                "in-progress",

            BridgeServiceState.Completed =>
                "completed",

            BridgeServiceState.Error =>
                "error",

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(state),
                    state,
                    "Unknown Bridge service state."
                )
        };
    }

    public static bool TryParseWireValue(
        string? value,
        out BridgeServiceState state
    )
    {
        switch (value)
        {
            case "in-progress":
                state =
                    BridgeServiceState.InProgress;

                return true;

            case "completed":
                state =
                    BridgeServiceState.Completed;

                return true;

            case "error":
                state =
                    BridgeServiceState.Error;

                return true;

            default:
                state =
                    BridgeServiceState.InProgress;

                return false;
        }
    }
}

internal readonly record struct
    BridgeServiceStatusReportRequest(
        BridgeServiceKind Service,
        BridgeServiceState State
    );

/*
 * Winrate and Rank normally run through their Bridge packet channels. In a
 * bot/sandbox match Panorama creates both results locally, so it reports the
 * local workflow through this small transport channel.
 *
 * Adviser and Hero Damage are deliberately excluded because their states come
 * exclusively from real Bridge-side workflows.
 */
internal static class BridgeServiceStatusReportService
{
    public const string Channel =
        "service-status";

    public static bool TryParse(
        IQueryCollection query,
        out BridgeServiceStatusReportRequest request,
        out string error
    )
    {
        request =
            default;

        error =
            String.Empty;

        var rawService =
            query["service"]
                .ToString();

        BridgeServiceKind service;

        switch (rawService)
        {
            case "winrate":
                service =
                    BridgeServiceKind.Winrate;

                break;

            case "rank":
                service =
                    BridgeServiceKind.Rank;

                break;

            default:
                error =
                    "service-status service must be " +
                    "winrate or rank.";

                return false;
        }

        if (
            !BridgeServiceStateText
                .TryParseWireValue(
                    query["state"]
                        .ToString(),
                    out var state
                )
        )
        {
            error =
                "Invalid service-status state.";

            return false;
        }

        request =
            new BridgeServiceStatusReportRequest(
                service,
                state
            );

        return true;
    }

    public static byte[] BuildPacket(
        BridgeServiceStatusStore statusStore,
        BridgeServiceStatusReportRequest request,
        long sessionOrdinal
    )
    {
        ArgumentNullException.ThrowIfNull(
            statusStore
        );

        var accepted =
            statusStore.ApplyReportedState(
                request.Service,
                request.State,
                sessionOrdinal
            );

        return BridgeProtocol.CreatePacket(
            BridgeMessageType.ServiceStatusAck,
            new byte[]
            {
                accepted
                    ? (byte)1
                    : (byte)0
            }
        );
    }
}

/*
 * One Panorama transport request consists of many PNG chunk requests. The
 * packet factory runs only once for a unique channel/session pair.
 *
 * LocalHostClient produces a strictly increasing numeric session for every
 * logical request. Keeping the latest session separately for each service
 * makes ordering independent from HTTP arrival order: a late response from an
 * older roster cannot overwrite a newer reset or request.
 */
internal sealed class BridgeServiceStatusStore
{
    private static readonly TimeSpan
        MinimumInProgressDuration =
            TimeSpan.FromMilliseconds(
                350
            );

    private readonly object _gate =
        new();

    private readonly long[] _sequences =
        new long[
            Enum.GetValues<BridgeServiceKind>()
                .Length
        ];

    private readonly long[]
        _latestSessionOrdinals =
            new long[
                Enum.GetValues<BridgeServiceKind>()
                    .Length
            ];

    private readonly DateTimeOffset[]
        _beganAtUtc =
            new DateTimeOffset[
                Enum.GetValues<BridgeServiceKind>()
                    .Length
            ];

    private readonly BridgeServiceState?[]
        _pendingStates =
            new BridgeServiceState?[
                Enum.GetValues<BridgeServiceKind>()
                    .Length
            ];

    private readonly DateTimeOffset[]
        _pendingVisibleAtUtc =
            new DateTimeOffset[
                Enum.GetValues<BridgeServiceKind>()
                    .Length
            ];

    private readonly bool[] _requestActive =
        new bool[
            Enum.GetValues<BridgeServiceKind>()
                .Length
        ];

    private readonly bool[]
        _inProgressObserved =
            new bool[
                Enum.GetValues<BridgeServiceKind>()
                    .Length
            ];

    private BridgeServiceStatusSnapshot _snapshot =
        BridgeServiceStatusSnapshot.InProgress;

    public bool TryBegin(
        BridgeServiceKind service,
        long sessionOrdinal,
        out BridgeServiceRequestToken token
    )
    {
        if (sessionOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionOrdinal),
                sessionOrdinal,
                "Service session ordinal must be positive."
            );
        }

        lock (_gate)
        {
            var index =
                GetIndex(
                    service
                );

            if (
                sessionOrdinal <=
                    _latestSessionOrdinals[index]
            )
            {
                token =
                    default;

                return false;
            }

            _latestSessionOrdinals[index] =
                sessionOrdinal;

            var sequence =
                ++_sequences[index];

            _beganAtUtc[index] =
                DateTimeOffset.UtcNow;

            _pendingStates[index] =
                null;

            _requestActive[index] =
                true;

            _inProgressObserved[index] =
                false;

            _snapshot =
                WithState(
                    _snapshot,
                    service,
                    BridgeServiceState.InProgress
                );

            token =
                new BridgeServiceRequestToken(
                    service,
                    sessionOrdinal,
                    sequence
                );

            return true;
        }
    }

    public bool Complete(
        BridgeServiceRequestToken token,
        BridgeServiceState state
    )
    {
        lock (_gate)
        {
            var index =
                GetIndex(
                    token.Service
                );

            if (
                token.SessionOrdinal !=
                    _latestSessionOrdinals[index] ||
                token.Sequence !=
                    _sequences[index]
            )
            {
                return false;
            }

            _requestActive[index] =
                false;

            if (
                state ==
                    BridgeServiceState.InProgress
            )
            {
                _pendingStates[index] =
                    null;

                _snapshot =
                    WithState(
                        _snapshot,
                        token.Service,
                        state
                    );

                return true;
            }

            var visibleAtUtc =
                _beganAtUtc[index] +
                MinimumInProgressDuration;

            if (
                !_inProgressObserved[index] ||
                DateTimeOffset.UtcNow <
                    visibleAtUtc
            )
            {
                _pendingStates[index] =
                    state;

                _pendingVisibleAtUtc[index] =
                    visibleAtUtc;
            }
            else
            {
                _pendingStates[index] =
                    null;

                _snapshot =
                    WithState(
                        _snapshot,
                        token.Service,
                        state
                    );
            }

            return true;
        }
    }

    public bool ApplyReportedState(
        BridgeServiceKind service,
        BridgeServiceState state,
        long sessionOrdinal
    )
    {
        if (
            service !=
                BridgeServiceKind.Winrate &&
            service !=
                BridgeServiceKind.Rank
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(service),
                service,
                "Only Winrate and Rank can be reported by Panorama."
            );
        }

        if (
            !TryBegin(
                service,
                sessionOrdinal,
                out var token
            )
        )
        {
            return false;
        }

        return Complete(
            token,
            state
        );
    }

    public void SetState(
        BridgeServiceKind service,
        BridgeServiceState state
    )
    {
        lock (_gate)
        {
            var index =
                GetIndex(
                    service
                );

            if (
                _snapshot.GetState(
                    service
                ) ==
                    state &&
                !_pendingStates[index]
                    .HasValue
            )
            {
                return;
            }

            ++_sequences[index];

            _pendingStates[index] =
                null;

            _requestActive[index] =
                false;

            _inProgressObserved[index] =
                false;

            _snapshot =
                WithState(
                    _snapshot,
                    service,
                    state
                );
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            /*
             * Keep the latest observed session watermark so repeated or late
             * chunks from an already known old session stay stale after the
             * clear. New LocalHostClient sessions are monotonic and will still
             * be accepted for the next match. Incrementing every sequence also
             * invalidates factories that were already running at the clear.
             */
            for (
                var index = 0;
                index < _sequences.Length;
                index++
            )
            {
                ++_sequences[index];
            }

            Array.Clear(
                _beganAtUtc,
                0,
                _beganAtUtc.Length
            );

            Array.Clear(
                _pendingStates,
                0,
                _pendingStates.Length
            );

            Array.Clear(
                _pendingVisibleAtUtc,
                0,
                _pendingVisibleAtUtc.Length
            );

            Array.Clear(
                _requestActive,
                0,
                _requestActive.Length
            );

            Array.Clear(
                _inProgressObserved,
                0,
                _inProgressObserved.Length
            );

            _snapshot =
                BridgeServiceStatusSnapshot
                    .InProgress;
        }
    }

    public void Reset(
        BridgeServiceKind service
    )
    {
        lock (_gate)
        {
            var index =
                GetIndex(
                    service
                );

            ++_sequences[index];

            _pendingStates[index] =
                null;

            _requestActive[index] =
                false;

            _inProgressObserved[index] =
                false;

            _snapshot =
                WithState(
                    _snapshot,
                    service,
                    BridgeServiceState.InProgress
                );
        }
    }

    public BridgeServiceStatusSnapshot
        GetSnapshot()
    {
        lock (_gate)
        {
            ApplyReadyCompletions();

            MarkInProgressObserved();

            return _snapshot;
        }
    }

    private void ApplyReadyCompletions()
    {
        var now =
            DateTimeOffset.UtcNow;

        foreach (
            var service in
                Enum.GetValues<BridgeServiceKind>()
        )
        {
            var index =
                GetIndex(
                    service
                );

            var pendingState =
                _pendingStates[index];

            if (
                !pendingState.HasValue ||
                !_inProgressObserved[index] ||
                now <
                    _pendingVisibleAtUtc[index]
            )
            {
                continue;
            }

            _pendingStates[index] =
                null;

            _snapshot =
                WithState(
                    _snapshot,
                    service,
                    pendingState.Value
                );
        }
    }

    private void MarkInProgressObserved()
    {
        foreach (
            var service in
                Enum.GetValues<BridgeServiceKind>()
        )
        {
            var index =
                GetIndex(
                    service
                );

            if (
                (
                    _requestActive[index] ||
                    _pendingStates[index]
                        .HasValue
                ) &&
                _snapshot.GetState(
                    service
                ) ==
                    BridgeServiceState.InProgress
            )
            {
                _inProgressObserved[index] =
                    true;
            }
        }
    }

    private static int GetIndex(
        BridgeServiceKind service
    )
    {
        var index =
            (int)service;

        if (
            index < 0 ||
            index >=
                Enum.GetValues<BridgeServiceKind>()
                    .Length
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(service),
                service,
                "Unknown Bridge service."
            );
        }

        return index;
    }

    private static BridgeServiceStatusSnapshot
        WithState(
            BridgeServiceStatusSnapshot snapshot,
            BridgeServiceKind service,
            BridgeServiceState state
        )
    {
        return service switch
        {
            BridgeServiceKind.Winrate =>
                snapshot with
                {
                    Winrate = state
                },

            BridgeServiceKind.Rank =>
                snapshot with
                {
                    Rank = state
                },

            BridgeServiceKind.Adviser =>
                snapshot with
                {
                    Adviser = state
                },

            BridgeServiceKind.HeroDamage =>
                snapshot with
                {
                    HeroDamage = state
                },

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(service),
                    service,
                    "Unknown Bridge service."
                )
        };
    }

}
