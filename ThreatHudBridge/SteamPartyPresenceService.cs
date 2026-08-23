using System.Diagnostics;
using System.Globalization;
using Steamworks;

internal sealed class SteamPartyPresenceService : IDisposable
{
    private const string PlayerGroupKey =
        "steam_player_group";

    private const string PlayerGroupSizeKey =
        "steam_player_group_size";

    /*
     * Steam allows up to 20 Rich Presence keys.
     *
     * 64 is kept as a defensive upper limit
     * in case the SDK changes or the native API
     * returns an invalid value.
     */
    private const int MaximumRichPresenceKeys =
        64;

    private readonly object _steamGate;

    private readonly object _stateGate =
        new();

    private readonly uint _deadlockAppId;

    private readonly TimeSpan _requestTimeout;

    private readonly Action<string> _log;

    /*
     * Only one diagnostic Rich Presence request
     * is executed at a time.
     *
     * The callback does not contain a request/session ID,
     * so overlapping requests would be
     * ambiguous.
     */
    private readonly SemaphoreSlim _requestGate =
        new(
            1,
            1
        );

    /*
     * The callback must be stored in a field
     * to prevent it from being collected by the GC.
     */
    private readonly Callback<
        FriendRichPresenceUpdate_t
    > _richPresenceCallback;

    private HashSet<ulong>?
        _pendingSteamIds;

    private Dictionary<ulong, uint>?
        _callbackAppIds;

    private TaskCompletionSource<bool>?
        _allCallbacksReceived;

    private bool _disposed;

    public SteamPartyPresenceService(
        object steamGate,
        uint deadlockAppId,
        TimeSpan requestTimeout,
        Action<string>? log = null
    )
    {
        _steamGate =
            steamGate ??
            throw new ArgumentNullException(
                nameof(steamGate)
            );

        if (deadlockAppId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadlockAppId),
                "deadlockAppId cannot be 0."
            );
        }

        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "requestTimeout must be greater than zero."
            );
        }

        _deadlockAppId =
            deadlockAppId;

        _requestTimeout =
            requestTimeout;

        _log =
            log ??
            (_ => { });

        _richPresenceCallback =
            Callback<
                FriendRichPresenceUpdate_t
            >.Create(
                OnFriendRichPresenceUpdate
            );
    }

    public async Task<
        SteamPartyPresenceSnapshot
    > GetSnapshotAsync(
        IReadOnlyList<CurrentMatchPlayer> players,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            players
        );

        ThrowIfDisposed();

        await _requestGate.WaitAsync(
            cancellationToken
        );

        try
        {
            ThrowIfDisposed();

            /*
             * BridgePayloadService has already performed
             * Steam Coplay discovery.
             *
             * Only impossible
             * and accidentally duplicated records are removed here.
             */
            var distinctPlayers =
                BuildDistinctPlayers(
                    players
                );

            /*
             * For the local user, the request
             * is not required: their Rich Presence is already
             * available in the local Steam client.
             */
            var requestedSteamIds =
                distinctPlayers
                    .Where(
                        player =>
                            !player.IsLocal
                    )
                    .Select(
                        player =>
                            player.SteamId64
                    )
                    .ToArray();

            var waitResult =
                await RequestAndWaitAsync(
                    requestedSteamIds,
                    cancellationToken
                );

            var callbackAppIds =
                CaptureCallbackAppIds();

            var playerSnapshots =
                ReadPlayerSnapshots(
                    distinctPlayers,
                    requestedSteamIds,
                    callbackAppIds
                );

            var groupCandidates =
                BuildGroupCandidates(
                    playerSnapshots
                );

            var callbacksReceived =
                requestedSteamIds.Count(
                    steamId =>
                        callbackAppIds
                            .ContainsKey(
                                steamId
                            )
                );

            var deadlockCallbacksReceived =
                requestedSteamIds.Count(
                    steamId =>
                        callbackAppIds.TryGetValue(
                            steamId,
                            out var appId
                        ) &&
                        appId ==
                            _deadlockAppId
                );

            var snapshot =
                new SteamPartyPresenceSnapshot(
                    Ok:
                        true,

                    GeneratedAtUtc:
                        DateTimeOffset.UtcNow,

                    PlayersFound:
                        playerSnapshots.Count,

                    RequestsSent:
                        requestedSteamIds.Length,

                    CallbacksReceived:
                        callbacksReceived,

                    DeadlockCallbacksReceived:
                        deadlockCallbacksReceived,

                    TimedOut:
                        waitResult.TimedOut,

                    WaitMilliseconds:
                        waitResult.ElapsedMilliseconds,

                    Players:
                        playerSnapshots,

                    GroupCandidates:
                        groupCandidates
                );

            _log(
                "Steam party presence: " +
                $"players={snapshot.PlayersFound}, " +
                $"requests={snapshot.RequestsSent}, " +
                $"callbacks={snapshot.CallbacksReceived}, " +
                $"deadlockCallbacks=" +
                $"{snapshot.DeadlockCallbacksReceived}, " +
                $"timedOut={snapshot.TimedOut}, " +
                $"groups={snapshot.GroupCandidates.Count}"
            );

            return snapshot;
        }
        finally
        {
            EndRequest();

            _requestGate.Release();
        }
    }

    private async Task<CallbackWaitResult>
        RequestAndWaitAsync(
            IReadOnlyList<ulong> steamIds,
            CancellationToken cancellationToken
        )
    {
        Task completion;

        /*
         * Pending state is created before native calls
         * so that even a very fast callback
         * is not lost.
         */
        lock (_stateGate)
        {
            _pendingSteamIds =
                new HashSet<ulong>(
                    steamIds
                );

            _callbackAppIds =
                new Dictionary<ulong, uint>();

            _allCallbacksReceived =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously
                );

            if (_pendingSteamIds.Count == 0)
            {
                _allCallbacksReceived
                    .TrySetResult(
                        true
                    );
            }

            completion =
                _allCallbacksReceived.Task;
        }

        /*
         * All Steamworks native calls go
         * through the same gate as the callback pump.
         */
        lock (_steamGate)
        {
            for (
                var index = 0;
                index < steamIds.Count;
                index++
            )
            {
                SteamFriends
                    .RequestFriendRichPresence(
                        new CSteamID(
                            steamIds[index]
                        )
                    );
            }
        }

        var startedAt =
            Stopwatch.GetTimestamp();

        var timeoutTask =
            Task.Delay(
                _requestTimeout,
                cancellationToken
            );

        var completedTask =
            await Task.WhenAny(
                completion,
                timeoutTask
            );

        var timedOut =
            completedTask !=
                completion;

        if (timedOut)
        {
            /*
             * On a normal timeout, await completes
             * normally.
             *
             * If the HTTP request is canceled,
             * OperationCanceledException is thrown here.
             */
            await timeoutTask;
        }
        else
        {
            await completion;
        }

        return new CallbackWaitResult(
            TimedOut:
                timedOut,

            ElapsedMilliseconds:
                checked(
                    (int)Math.Round(
                        Stopwatch
                            .GetElapsedTime(
                                startedAt
                            )
                            .TotalMilliseconds
                    )
                )
        );
    }

    private void OnFriendRichPresenceUpdate(
        FriendRichPresenceUpdate_t callback
    )
    {
        TaskCompletionSource<bool>?
            completion =
                null;

        lock (_stateGate)
        {
            if (
                _pendingSteamIds is null ||
                _callbackAppIds is null
            )
            {
                return;
            }

            var steamId64 =
                callback
                    .m_steamIDFriend
                    .m_SteamID;

            /*
             * Automatic callbacks for other
             * users or callbacks from the previous
             * request are ignored.
             */
            if (
                !_pendingSteamIds.Remove(
                    steamId64
                )
            )
            {
                return;
            }

            _callbackAppIds[steamId64] =
                callback
                    .m_nAppID
                    .m_AppId;

            if (_pendingSteamIds.Count == 0)
            {
                completion =
                    _allCallbacksReceived;
            }
        }

        completion?.TrySetResult(
            true
        );
    }

    private IReadOnlyList<
        SteamPartyPresencePlayer
    > ReadPlayerSnapshots(
        IReadOnlyList<CurrentMatchPlayer> players,
        IReadOnlyCollection<ulong> requestedSteamIds,
        IReadOnlyDictionary<ulong, uint>
            callbackAppIds
    )
    {
        var requestedSet =
            new HashSet<ulong>(
                requestedSteamIds
            );

        var result =
            new List<SteamPartyPresencePlayer>(
                players.Count
            );

        lock (_steamGate)
        {
            for (
                var index = 0;
                index < players.Count;
                index++
            )
            {
                var player =
                    players[index];

                var steamId =
                    new CSteamID(
                        player.SteamId64
                    );

                var allKeys =
                    ReadAllRichPresenceKeys(
                        steamId
                    );

                /*
                 * These two keys are also read
                 * directly in case Steam
                 * did not include them in debug enumeration.
                 */
                var groupId =
                    ReadKnownValue(
                        steamId,
                        allKeys,
                        PlayerGroupKey
                    );

                var groupSizeRaw =
                    ReadKnownValue(
                        steamId,
                        allKeys,
                        PlayerGroupSizeKey
                    );

                AddKnownValue(
                    allKeys,
                    PlayerGroupKey,
                    groupId
                );

                AddKnownValue(
                    allKeys,
                    PlayerGroupSizeKey,
                    groupSizeRaw
                );

                var callbackReceived =
                    callbackAppIds.TryGetValue(
                        player.SteamId64,
                        out var callbackAppId
                    );

                result.Add(
                    new SteamPartyPresencePlayer(
                        AccountId:
                            player.AccountId,

                        /*
                         * SteamID64 is passed as a string:
                         * its value exceeds the safe
                         * integer range JavaScript.
                         */
                        SteamId64:
                            player.SteamId64
                                .ToString(
                                    CultureInfo
                                        .InvariantCulture
                                ),

                        PersonaName:
                            player.PersonaName,

                        IsLocal:
                            player.IsLocal,

                        RequestSent:
                            requestedSet.Contains(
                                player.SteamId64
                            ),

                        CallbackReceived:
                            callbackReceived,

                        CallbackAppId:
                            callbackReceived
                                ? callbackAppId
                                : null,

                        CallbackMatchesDeadlock:
                            callbackReceived &&
                            callbackAppId ==
                                _deadlockAppId,

                        RichPresenceKeyCount:
                            allKeys.Count,

                        SteamPlayerGroup:
                            EmptyToNull(
                                groupId
                            ),

                        SteamPlayerGroupSizeRaw:
                            EmptyToNull(
                                groupSizeRaw
                            ),

                        SteamPlayerGroupSize:
                            TryParsePositiveInt(
                                groupSizeRaw
                            ),

                        AllKeys:
                            allKeys
                    )
                );
            }
        }

        return result;
    }

    private static Dictionary<string, string>
        ReadAllRichPresenceKeys(
            CSteamID steamId
        )
    {
        var keyCount =
            Math.Clamp(
                SteamFriends
                    .GetFriendRichPresenceKeyCount(
                        steamId
                    ),
                0,
                MaximumRichPresenceKeys
            );

        var result =
            new Dictionary<string, string>(
                StringComparer.Ordinal
            );

        for (
            var index = 0;
            index < keyCount;
            index++
        )
        {
            var key =
                SteamFriends
                    .GetFriendRichPresenceKeyByIndex(
                        steamId,
                        index
                    ) ??
                string.Empty;

            if (
                string.IsNullOrWhiteSpace(
                    key
                ) ||
                result.ContainsKey(
                    key
                )
            )
            {
                continue;
            }

            result[key] =
                SteamFriends
                    .GetFriendRichPresence(
                        steamId,
                        key
                    ) ??
                string.Empty;
        }

        return result;
    }

    private static string ReadKnownValue(
        CSteamID steamId,
        IReadOnlyDictionary<string, string> allKeys,
        string key
    )
    {
        return allKeys.TryGetValue(
            key,
            out var value
        )
            ? value
            : (
                SteamFriends
                    .GetFriendRichPresence(
                        steamId,
                        key
                    ) ??
                string.Empty
            );
    }

    private static void AddKnownValue(
        IDictionary<string, string> allKeys,
        string key,
        string value
    )
    {
        if (
            !string.IsNullOrWhiteSpace(
                value
            ) &&
            !allKeys.ContainsKey(
                key
            )
        )
        {
            allKeys[key] =
                value;
        }
    }

    /*
     * Grouping is performed for all players
     * without separating allies/enemies.
     *
     * Later Panorama can map accountId
     * to its team and native player panel.
     */
    private static IReadOnlyList<
        SteamPartyGroupCandidate
    > BuildGroupCandidates(
        IReadOnlyList<
            SteamPartyPresencePlayer
        > players
    )
    {
        return players
            .Where(
                player =>
                    !string.IsNullOrWhiteSpace(
                        player.SteamPlayerGroup
                    )
            )
            .GroupBy(
                player =>
                    player.SteamPlayerGroup!,

                StringComparer.Ordinal
            )
            .Select(
                group =>
                {
                    var members =
                        group.ToArray();

                    var declaredSizes =
                        members
                            .Where(
                                member =>
                                    member
                                        .SteamPlayerGroupSize
                                        .HasValue
                            )
                            .Select(
                                member =>
                                    member
                                        .SteamPlayerGroupSize!
                                        .Value
                            )
                            .Distinct()
                            .OrderBy(
                                size =>
                                    size
                            )
                            .ToArray();

                    int? declaredSize =
                        declaredSizes.Length == 1
                            ? declaredSizes[0]
                            : null;

                    return new SteamPartyGroupCandidate(
                        GroupId:
                            group.Key,

                        ObservedPlayers:
                            members.Length,

                        DeclaredSize:
                            declaredSize,

                        DeclaredSizes:
                            declaredSizes,

                        DeclaredSizeConflict:
                            declaredSizes.Length > 1,

                        IncludesLocalPlayer:
                            members.Any(
                                member =>
                                    member.IsLocal
                            ),

                        /*
                         * This is only a candidate.
                         *
                         * It should only be called a party
                         * after verifying it in Deadlock.
                         */
                        IsMultiPlayerCandidate:
                            members.Length >= 2,

                        IsCompleteByDeclaredSize:
                            declaredSize.HasValue &&
                            declaredSize.Value ==
                                members.Length,

                        AccountIds:
                            members
                                .Select(
                                    member =>
                                        member.AccountId
                                )
                                .ToArray(),

                        SteamIds64:
                            members
                                .Select(
                                    member =>
                                        member.SteamId64
                                )
                                .ToArray(),

                        PersonaNames:
                            members
                                .Select(
                                    member =>
                                        member.PersonaName
                                )
                                .ToArray()
                    );
                }
            )
            .OrderByDescending(
                group =>
                    group.ObservedPlayers
            )
            .ThenBy(
                group =>
                    group.GroupId,

                StringComparer.Ordinal
            )
            .ToArray();
    }

    private static IReadOnlyList<CurrentMatchPlayer>
        BuildDistinctPlayers(
            IReadOnlyList<CurrentMatchPlayer> players
        )
    {
        var seenSteamIds =
            new HashSet<ulong>();

        var result =
            new List<CurrentMatchPlayer>(
                players.Count
            );

        for (
            var index = 0;
            index < players.Count;
            index++
        )
        {
            var player =
                players[index];

            if (
                player.SteamId64 == 0 ||
                player.AccountId == 0 ||
                !seenSteamIds.Add(
                    player.SteamId64
                )
            )
            {
                continue;
            }

            result.Add(
                player
            );
        }

        return result;
    }

    private Dictionary<ulong, uint>
        CaptureCallbackAppIds()
    {
        lock (_stateGate)
        {
            return _callbackAppIds is null
                ? new Dictionary<ulong, uint>()
                : new Dictionary<ulong, uint>(
                    _callbackAppIds
                );
        }
    }

    private void EndRequest()
    {
        lock (_stateGate)
        {
            _pendingSteamIds =
                null;

            _callbackAppIds =
                null;

            _allCallbacksReceived =
                null;
        }
    }

    private static int? TryParsePositiveInt(
        string value
    )
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed
        ) &&
        parsed > 0

            ? parsed
            : null;
    }

    private static string? EmptyToNull(
        string value
    )
    {
        return string.IsNullOrWhiteSpace(
            value
        )
            ? null
            : value;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(
                    SteamPartyPresenceService
                )
            );
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed =
            true;

        lock (_stateGate)
        {
            _allCallbacksReceived?
                .TrySetCanceled();

            _pendingSteamIds =
                null;

            _callbackAppIds =
                null;

            _allCallbacksReceived =
                null;
        }

        _richPresenceCallback.Dispose();

        _requestGate.Dispose();
    }

    private readonly record struct
        CallbackWaitResult(
            bool TimedOut,
            int ElapsedMilliseconds
        );
}

internal sealed record SteamPartyPresenceSnapshot(
    bool Ok,
    DateTimeOffset GeneratedAtUtc,
    int PlayersFound,
    int RequestsSent,
    int CallbacksReceived,
    int DeadlockCallbacksReceived,
    bool TimedOut,
    int WaitMilliseconds,
    IReadOnlyList<SteamPartyPresencePlayer> Players,
    IReadOnlyList<SteamPartyGroupCandidate>
        GroupCandidates
);

internal sealed record SteamPartyPresencePlayer(
    uint AccountId,
    string SteamId64,
    string PersonaName,
    bool IsLocal,
    bool RequestSent,
    bool CallbackReceived,
    uint? CallbackAppId,
    bool CallbackMatchesDeadlock,
    int RichPresenceKeyCount,
    string? SteamPlayerGroup,
    string? SteamPlayerGroupSizeRaw,
    int? SteamPlayerGroupSize,
    IReadOnlyDictionary<string, string> AllKeys
);

internal sealed record SteamPartyGroupCandidate(
    string GroupId,
    int ObservedPlayers,
    int? DeclaredSize,
    IReadOnlyList<int> DeclaredSizes,
    bool DeclaredSizeConflict,
    bool IncludesLocalPlayer,
    bool IsMultiPlayerCandidate,
    bool IsCompleteByDeclaredSize,
    IReadOnlyList<uint> AccountIds,
    IReadOnlyList<string> SteamIds64,
    IReadOnlyList<string> PersonaNames
);