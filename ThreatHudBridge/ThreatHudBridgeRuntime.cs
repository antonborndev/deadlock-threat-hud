using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Steamworks;

internal enum BridgeRuntimeExitReason
{
    DeadlockExited,
    ShutdownRequested,
    StartupFailed
}

internal sealed class ThreatHudBridgeRuntime :
    IDisposable
{
    private const uint DeadlockAppId =
        1422450;

    private const int MaximumCoplayPlayers =
        100;

    private const string DeadlockProcessName =
        "deadlock";

    private const string HttpAddress =
        "http://127.0.0.1:28741";

    private const string CurrentMatchResultChannel =
        "current-match-result";

    private static readonly byte[]
        TransparentPixelPng =
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ" +
                "AAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg=="
            );

    private readonly string[] _args;
    private readonly IBridgeRuntimeView _view;

    private readonly object _steamGate =
        new();

    private readonly CancellationTokenSource
        _stopCancellation =
            new();

    private int _runStarted;
    private int _stopRequested;
    private bool _disposed;

    public ThreatHudBridgeRuntime(
        string[] args,
        IBridgeRuntimeView view
    )
    {
        _args =
            args ??
            Array.Empty<string>();

        _view =
            view ??
            throw new ArgumentNullException(
                nameof(view)
            );
    }

    public void RequestStop()
    {
        if (
            Interlocked.Exchange(
                ref _stopRequested,
                1
            ) != 0
        )
        {
            return;
        }

        _view.SetRuntimeState(
            BridgeRuntimeState.Stopping,
            "Shutting down Threat HUD Bridge"
        );

        try
        {
            _stopCancellation.Cancel();
        }
        catch (
            ObjectDisposedException
        )
        {
        }
    }

    public async Task<BridgeRuntimeExitReason>
        RunAsync()
    {
        if (
            Interlocked.Exchange(
                ref _runStarted,
                1
            ) != 0
        )
        {
            throw new InvalidOperationException(
                "ThreatHudBridgeRuntime is already running."
            );
        }

        try
        {
            return await RunDeadlockSessionAsync(
                _stopCancellation.Token
            );
        }
        catch (
            OperationCanceledException
        )
        when (
            _stopCancellation
                .IsCancellationRequested
        )
        {
            SetInactiveState();

            _view.SetRuntimeState(
                BridgeRuntimeState.Stopped,
                "Threat HUD Bridge stopped"
            );

            return BridgeRuntimeExitReason
                .ShutdownRequested;
        }
        catch (Exception error)
        {
            SetInactiveState();

            LogException(
                "Unhandled Bridge error",
                error
            );

            _view.SetRuntimeState(
                BridgeRuntimeState.Error,
                error.Message
            );

            return BridgeRuntimeExitReason
                .StartupFailed;
        }
    }

    private async Task<BridgeRuntimeExitReason>
        RunDeadlockSessionAsync(
            CancellationToken cancellationToken
        )
    {
        var steamInitialized =
            false;

        try
        {
            SetWaitingState(
                "Waiting for Deadlock to start"
            );

            Log(
                "Waiting for the Deadlock process..."
            );

            await WaitForDeadlockReadyAsync(
                cancellationToken
            );

            _view.SetDeadlockRunning(
                true
            );

            Log(
                "Deadlock process detected."
            );

            _view.SetRuntimeState(
                BridgeRuntimeState.StartingSteam,
                "Initializing Steam API"
            );

            if (!SteamAPI.Init())
            {
                var message =
                    "SteamAPI.Init() returned false " +
                    "after Deadlock started.";

                LogError(
                    message
                );

                _view.SetRuntimeState(
                    BridgeRuntimeState.Error,
                    message
                );

                return BridgeRuntimeExitReason
                    .StartupFailed;
            }

            steamInitialized =
                true;

            _view.SetSteamInitialized(
                true
            );

            Log(
                "Steam API initialized."
            );

            var ownSteamId =
                SteamUser.GetSteamID();

            var ownSteamId64 =
                ownSteamId
                    .m_SteamID
                    .ToString();

            var ownAccountId =
                unchecked(
                    (uint)(
                        ownSteamId.m_SteamID &
                        0xFFFFFFFFUL
                    )
                );

            _view.SetAccountId(
                ownAccountId
            );

            Log(
                $"My accountID: {ownAccountId}"
            );

            return await RunHostedBridgeAsync(
                ownSteamId,
                ownSteamId64,
                ownAccountId,
                cancellationToken
            );
        }
        finally
        {
            if (steamInitialized)
            {
                try
                {
                    lock (_steamGate)
                    {
                        SteamAPI.Shutdown();
                    }

                    Log(
                        "Steam API stopped."
                    );
                }
                catch (Exception error)
                {
                    LogException(
                        "SteamAPI.Shutdown() error",
                        error
                    );
                }
            }

            SetInactiveState();
        }
    }

    private void SetWaitingState(
        string detail
    )
    {
        _view.SetRuntimeState(
            BridgeRuntimeState.WaitingForDeadlock,
            detail
        );

        SetInactiveState();
    }

    private void SetInactiveState()
    {
        _view.SetHttpServerRunning(
            false
        );

        _view.SetSteamInitialized(
            false
        );

        _view.SetDeadlockRunning(
            false
        );

        _view.SetAccountId(
            null
        );
    }

    private async Task<BridgeRuntimeExitReason>
        RunHostedBridgeAsync(
            CSteamID ownSteamId,
            string ownSteamId64,
            uint ownAccountId,
            CancellationToken cancellationToken
        )
    {
        Action<string> silentLog =
            static _ => { };

        var payloadService =
            new BridgePayloadService(
                _steamGate,
                ownSteamId,
                DeadlockAppId,
                MaximumCoplayPlayers,
                silentLog
            );

        using var partyPresenceService =
            new SteamPartyPresenceService(
                _steamGate,
                DeadlockAppId,

                requestTimeout:
                    TimeSpan.FromSeconds(3),

                log:
                    Log
            );

        var transport =
            new PngDimensionTransport(
                sessionLifetime:
                    TimeSpan.FromMinutes(2),

                log:
                    silentLog
            );

        var serviceStatusStore =
            new BridgeServiceStatusStore();

        using var deadlockApiHttpClient =
            new HttpClient
            {
                BaseAddress =
                    new Uri(
                        "https://api.deadlock-api.com/"
                    ),

                Timeout =
                    TimeSpan.FromSeconds(
                        30
                    )
            };

        deadlockApiHttpClient
            .DefaultRequestHeaders
            .Accept
            .ParseAdd(
                "application/json"
            );

        deadlockApiHttpClient
            .DefaultRequestHeaders
            .UserAgent
            .ParseAdd(
                "DeadlockThreatHud/1.0"
            );

        await using var currentMatchContextService =
            new CurrentMatchContextService(
                Log
            );


        var deadlockApiClient =
            new DeadlockApiClient(
                deadlockApiHttpClient
            );

        using var playerStatsService =
            new DeadlockPlayerStatsService(
                deadlockApiClient,

                cacheLifetime:
                    TimeSpan.FromMinutes(2),

                log:
                    silentLog
            );

        using var heroCatalogService =
            new DeadlockHeroCatalogService(
                deadlockApiHttpClient,

                cacheLifetime:
                    TimeSpan.FromHours(6),

                log:
                    silentLog
            );

        await using var laneAdvisorService =
            new DeadlockLaneAdvisorService(
                heroCatalogService,
                deadlockApiClient,
                cancellationToken
            );

        using var playerRankService =
            new DeadlockPlayerRankService(
                deadlockApiHttpClient,

                cacheLifetime:
                    TimeSpan.FromMinutes(10),

                maximumConcurrency:
                    4
            );

        var currentMatchPlayerRanksService =
            new CurrentMatchPlayerRanksService(
                playerRankService
            );

        await using var matchPlayerDetailsService =
            new CurrentMatchPlayerDetailsCoordinator(
                heroCatalogService,
                playerStatsService,
                playerRankService,
                cancellationToken,
                Log,
                ready =>
                    currentMatchContextService
                        .NotifyCurrentHeroStatsReady(
                            ready.MatchId,
                            ready.GeneratedAtUtc
                        )
            );

        /*
         * Match state, player details and lane results must be sampled under
         * the same gate that protects Panorama-driven match transitions.
         */
        var currentMatchLifecycleGate =
            new object();

        var matchHistoryStore =
            new MatchHistoryStore();

        var matchHistoryCaptureService =
            new MatchHistoryCaptureService(
                currentMatchLifecycleGate,
                currentMatchContextService,
                matchPlayerDetailsService,
                serviceStatusStore,
                laneAdvisorService,
                ownAccountId,
                matchHistoryStore,
                Log
            );

        Log(
            "Match history DB: " +
            matchHistoryStore.DatabasePath
        );

        var reactionStore =
            new PlayerHeroReactionStore();

        await reactionStore.InitializeAsync(
            cancellationToken
        );

        Log(
            "Player reactions DB: " +
            reactionStore.DatabasePath
        );

        var currentMatchPlayerStatsService =
            new CurrentMatchPlayerStatsService(
                heroCatalogService,
                playerStatsService,
                reactionStore,
                Log
            );

        var playerHeroReactionWriteService =
            new PlayerHeroReactionWriteService(
                reactionStore,
                Log
            );

        using var rankImageService =
            new DeadlockRankImageService(
                deadlockApiHttpClient,

                cacheLifetime:
                    TimeSpan.FromHours(6)
            );

        WebApplication? app =
            null;

        CancellationTokenSource?
            callbackCancellation =
                null;

        Task? callbackTask =
            null;

        Task? matchHistoryTask =
            null;

        var appStarted =
            false;

        try
        {
            app =
                BuildWebApplication(
                    payloadService,
                    partyPresenceService,
                    currentMatchContextService,
                    transport,
                    serviceStatusStore,
                    currentMatchPlayerStatsService,
                    playerHeroReactionWriteService,
                    currentMatchPlayerRanksService,
                    rankImageService,
                    playerStatsService,
                    heroCatalogService,
                    laneAdvisorService,
                    matchPlayerDetailsService,
                    ownSteamId64,
                    ownAccountId,
                    currentMatchLifecycleGate,
                    matchHistoryCaptureService
                );

            callbackCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken
                    );

            callbackTask =
                Task.Run(
                    () =>
                        RunSteamCallbacksAsync(
                            callbackCancellation.Token
                        )
                );

            matchHistoryTask =
                Task.Run(
                    () =>
                        matchHistoryCaptureService
                            .RunAsync(
                                callbackCancellation.Token
                            )
                );

            _view.SetRuntimeState(
                BridgeRuntimeState.StartingHttpServer,
                $"Starting {HttpAddress}"
            );

            Log(
                $"Starting HTTP API: {HttpAddress}"
            );

            await app.StartAsync(
                cancellationToken
            );

            appStarted =
                true;

            _view.SetHttpServerRunning(
                true,
                HttpAddress
            );

            _view.SetRuntimeState(
                BridgeRuntimeState.Running,
                "Bridge is ready"
            );

            Log(
                $"HTTP API: {HttpAddress}"
            );

            var exitReason =
                await WaitForExitReasonAsync(
                    cancellationToken
                );

            _view.SetRuntimeState(
                BridgeRuntimeState.Stopping,

                exitReason ==
                    BridgeRuntimeExitReason
                        .DeadlockExited
                    ? "Deadlock closed"
                    : "Shutdown requested"
            );

            return exitReason;
        }
        finally
        {
            if (app is not null)
            {
                if (appStarted)
                {
                    try
                    {
                        using var stopTimeout =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(5)
                            );

                        await app.StopAsync(
                            stopTimeout.Token
                        );

                        Log(
                            "HTTP API stopped."
                        );
                    }
                    catch (Exception error)
                    {
                        LogException(
                            "HTTP API shutdown error",
                            error
                        );
                    }
                }

                try
                {
                    await app.DisposeAsync();
                }
                catch (Exception error)
                {
                    LogException(
                        "HTTP API disposal error",
                        error
                    );
                }
            }

            _view.SetHttpServerRunning(
                false
            );

            if (callbackCancellation is not null)
            {
                try
                {
                    callbackCancellation.Cancel();
                }
                catch (
                    ObjectDisposedException
                )
                {
                }
            }

            if (callbackTask is not null)
            {
                try
                {
                    await callbackTask;
                }
                catch (
                    OperationCanceledException
                )
                {
                }
                catch (Exception error)
                {
                    LogException(
                        "Steam callback task error",
                        error
                    );
                }
            }

            if (matchHistoryTask is not null)
            {
                try
                {
                    await matchHistoryTask;
                }
                catch (
                    OperationCanceledException
                )
                {
                }
                catch (Exception error)
                {
                    LogException(
                        "Match history task error",
                        error
                    );
                }
            }

            try
            {
                /*
                 * Stop the periodic writer before the final flush so the
                 * independent shutdown token never races for _captureGate.
                 */
                using var historyFlushTimeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5)
                    );

                await matchHistoryCaptureService
                    .CaptureNowAsync(
                        historyFlushTimeout.Token
                    );
            }
            catch (Exception error)
            {
                LogException(
                    "Match history final snapshot error",
                    error
                );
            }

            callbackCancellation?.Dispose();
        }
    }

    private WebApplication BuildWebApplication(
        BridgePayloadService payloadService,
        SteamPartyPresenceService partyPresenceService,
        CurrentMatchContextService
            currentMatchContextService,
        PngDimensionTransport transport,
        BridgeServiceStatusStore
            serviceStatusStore,
        CurrentMatchPlayerStatsService
            currentMatchPlayerStatsService,
        PlayerHeroReactionWriteService
            playerHeroReactionWriteService,
        CurrentMatchPlayerRanksService
            currentMatchPlayerRanksService,
        DeadlockRankImageService rankImageService,
        DeadlockPlayerStatsService playerStatsService,
        DeadlockHeroCatalogService heroCatalogService,
        DeadlockLaneAdvisorService laneAdvisorService,
        CurrentMatchPlayerDetailsCoordinator matchPlayerDetailsService,
        string ownSteamId64,
        uint ownAccountId,
        object currentMatchLifecycleGate,
        MatchHistoryCaptureService matchHistoryCaptureService
    )
    {
        var builder =
            WebApplication.CreateBuilder(
                _args
            );

        builder.Logging.ClearProviders();

        builder.WebHost.UseUrls(
            HttpAddress
        );

        var app =
            builder.Build();

        app.Use(
            async (
                context,
                next
            ) =>
            {
                context.Response.Headers[
                    "Access-Control-Allow-Origin"
                ] = "*";

                context.Response.Headers[
                    "Access-Control-Allow-Methods"
                ] = "GET, POST, OPTIONS";

                context.Response.Headers[
                    "Access-Control-Allow-Headers"
                ] = "Content-Type";

                context.Response.Headers[
                    "Cache-Control"
                ] =
                    "no-store, no-cache, must-revalidate";

                context.Response.Headers[
                    "Pragma"
                ] = "no-cache";

                context.Response.Headers[
                    "Expires"
                ] = "0";

                if (
                    HttpMethods.IsOptions(
                        context.Request.Method
                    )
                )
                {
                    context.Response.StatusCode =
                        StatusCodes
                            .Status204NoContent;

                    return;
                }

                await next();
            }
        );

        app.MapGet(
            "/health",

            () =>
            {
                BridgeServiceStatusSnapshot
                    serviceStatuses;

                bool hasCurrentMatch;

                lock (currentMatchLifecycleGate)
                {
                    if (!IsHeroDamageModuleEnabled())
                    {
                        currentMatchContextService
                            .DisableHeroDamageForCurrentMatch();
                    }

                    var matchSnapshot =
                        currentMatchContextService
                            .GetSnapshot();

                    hasCurrentMatch =
                        matchSnapshot.HasMatch;

                    if (
                        hasCurrentMatch &&
                        matchSnapshot
                            .HeroDamageAllowedForMatch
                    )
                    {
                        serviceStatusStore.SetState(
                            BridgeServiceKind
                                .HeroDamage,

                            GetHeroDamageServiceState(
                                matchSnapshot
                            )
                        );
                    }
                    else if (hasCurrentMatch)
                    {
                        serviceStatusStore.Reset(
                            BridgeServiceKind
                                .HeroDamage
                        );
                    }

                    serviceStatuses =
                        serviceStatusStore
                            .GetSnapshot();
                }

                return Results.Json(
                    new
                    {
                        ok = true,
                        appId = DeadlockAppId,
                        ownSteamId64,
                        ownAccountId,

                        hasCurrentMatch =
                            hasCurrentMatch,

                        protocolVersion =
                            BridgeProtocol.Version,

                        deadlockApi =
                            "https://api.deadlock-api.com",

                        services =
                            new
                            {
                                winrate =
                                    BridgeServiceStateText
                                        .ToWireValue(
                                            serviceStatuses
                                                .Winrate
                                        ),

                                rank =
                                    BridgeServiceStateText
                                        .ToWireValue(
                                            serviceStatuses
                                                .Rank
                                        ),

                                adviser =
                                    BridgeServiceStateText
                                        .ToWireValue(
                                            serviceStatuses
                                                .Adviser
                                        ),

                                heroDamage =
                                    BridgeServiceStateText
                                        .ToWireValue(
                                            serviceStatuses
                                                .HeroDamage
                                        )
                            }
                    }
                );
            }
        );

        app.MapGet(
            "/current-match-context.png",

            (
                HttpContext context
            ) =>
            {
                var rawMatchId =
                    context.Request.Query[
                        "matchId"
                    ].ToString();

                if (
                    !ulong.TryParse(
                        rawMatchId,
                        out var matchId
                    )
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "matchId parameter must be " +
                                "an unsigned integer."
                        }
                    );
                }

                var clearGeneratedPngCache =
                    false;

                var matchChanged =
                    false;

                lock (currentMatchLifecycleGate)
                {
                    var previousMatchId =
                        currentMatchContextService
                            .GetSnapshot()
                            .MatchId;

                    if (
                        previousMatchId != 0 &&
                        previousMatchId != matchId
                    )
                    {
                        /*
                         * Freeze the completed match before any transition
                         * clears its players, lane result or live damage.
                         * SQLite I/O is performed later, outside this gate.
                         */
                        matchHistoryCaptureService
                            .QueueCurrentSnapshotWhileLocked();
                    }

                    matchChanged =
                        currentMatchContextService
                            .Update(
                                matchId,
                                IsHeroDamageModuleEnabled()
                            );

                    if (matchChanged)
                    {
                        matchPlayerDetailsService
                            .TransitionToMatch(
                                matchId
                            );

                        if (matchId == 0)
                        {
                            /*
                             * The same confirmed clear that moves CURRENT HERO
                             * STATS to Waiting prepares all service workflows
                             * for the next match. No second match watcher is
                             * involved.
                             */
                            laneAdvisorService
                                .ResetForMatchTransition();

                            serviceStatusStore.ResetAll();

                            clearGeneratedPngCache =
                                true;
                        }
                        else
                        {
                            /*
                             * The non-zero beacon may arrive after one-shot
                             * Winrate/Rank requests. Preserve their results and
                             * reset the match-bound damage stream. Adviser is
                             * also reset below only for a direct transition
                             * from one non-zero match id to another.
                             */
                            serviceStatusStore.Reset(
                                BridgeServiceKind
                                    .HeroDamage
                            );

                            if (
                                previousMatchId != 0 &&
                                previousMatchId !=
                                    matchId
                            )
                            {
                                /*
                                 * A direct non-zero match transition must
                                 * not expose the completed Adviser snapshot
                                 * from the previous roster. The usual
                                 * zero-to-non-zero beacon still preserves a
                                 * one-shot Adviser result that may have
                                 * arrived before the match id.
                                 */
                                laneAdvisorService
                                    .ResetForMatchTransition();

                                serviceStatusStore.Reset(
                                    BridgeServiceKind
                                        .Adviser
                                );
                            }
                        }
                    }
                }

                if (
                    matchChanged &&
                    matchId != 0
                )
                {
                    /*
                     * Create the list entry immediately. The periodic capture
                     * will replace this initial snapshot as details arrive.
                     */
                    matchHistoryCaptureService
                        .RequestCapture();
                }

                if (clearGeneratedPngCache)
                {
                    /*
                     * Release generated PNG bodies after the match, but keep
                     * bounded transport sessions until their normal TTL. A
                     * late chunk can then finish against the original packet
                     * instead of recreating packetFactory after match clear.
                     */
                    transport.ClearGeneratedPngCache();
                }

                return Results.File(
                    TransparentPixelPng,
                    "image/png"
                );
            }
        );

        app.MapPost(
            "/hero-damage-module-state-changed",

            (
                HttpContext context
            ) =>
            {
                var rawEnabled =
                    context.Request.Query[
                        "enabled"
                    ].ToString();

                bool heroDamageEnabled;

                if (rawEnabled == "1")
                {
                    heroDamageEnabled =
                        true;
                }
                else if (rawEnabled == "0")
                {
                    heroDamageEnabled =
                        false;
                }
                else
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "enabled parameter must be 0 or 1."
                        }
                    );
                }

                var rawChangedAtUtcTicks =
                    context.Request.Query[
                        "changedAtUtcTicks"
                    ].ToString();

                if (
                    !long.TryParse(
                        rawChangedAtUtcTicks,
                        out var changedAtUtcTicks
                    ) ||
                    changedAtUtcTicks <
                        DateTime.MinValue.Ticks ||
                    changedAtUtcTicks >
                        DateTime.MaxValue.Ticks
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "changedAtUtcTicks parameter is invalid."
                        }
                    );
                }

                var changedAtUtc =
                    new DateTimeOffset(
                        new DateTime(
                            changedAtUtcTicks,
                            DateTimeKind.Utc
                        )
                    );

                bool stoppedForCurrentMatch;
                bool ignoredStaleTransition;
                CurrentMatchContextSnapshot snapshot;

                lock (currentMatchLifecycleGate)
                {
                    var snapshotBeforeChange =
                        currentMatchContextService
                            .GetSnapshot();

                    var matchObservedAtUtc =
                        snapshotBeforeChange
                            .MatchObservedAtUtc;

                    /*
                     * Honor the immutable UI transition carried by this
                     * request. Two rapid Off/On posts may arrive out of order;
                     * Off must still latch the current match as blocked, while
                     * On remains a deliberate no-op until the next match. An
                     * Off from before the current match is ignored only when
                     * persistence has since returned to On; this keeps a
                     * delayed request from disabling the next match.
                     */
                    ignoredStaleTransition =
                        !heroDamageEnabled &&
                        matchObservedAtUtc.HasValue &&
                        changedAtUtc <
                            matchObservedAtUtc.Value &&
                        IsHeroDamageModuleEnabled();

                    stoppedForCurrentMatch =
                        !heroDamageEnabled &&
                        !ignoredStaleTransition &&
                        currentMatchContextService
                            .DisableHeroDamageForCurrentMatch();

                    snapshot =
                        currentMatchContextService
                            .GetSnapshot();

                    if (
                        snapshot.HasMatch &&
                        !snapshot
                            .HeroDamageAllowedForMatch
                    )
                    {
                        serviceStatusStore.Reset(
                            BridgeServiceKind
                                .HeroDamage
                        );
                    }
                }

                return Results.Json(
                    new
                    {
                        ok = true,
                        heroDamageEnabled,
                        stoppedForCurrentMatch,
                        ignoredStaleTransition,
                        matchId = snapshot.MatchId,

                        allowedForCurrentMatch =
                            snapshot
                                .HeroDamageAllowedForMatch
                    }
                );
            }
        );

        app.MapGet(
            "/current-match-context-diagnostics",

            () =>
            {
                lock (currentMatchLifecycleGate)
                {
                    var snapshot =
                        currentMatchContextService
                            .GetSnapshot();

                    var serviceStatuses =
                        serviceStatusStore
                            .GetSnapshot();

                    var currentLaneStats =
                        snapshot.MatchId != 0 &&
                        serviceStatuses.Adviser ==
                            BridgeServiceState.Completed
                            ? laneAdvisorService
                                .GetCurrentLaneStatsSnapshot()
                            : null;

                    return Results.Json(
                        new
                        {
                            ok = true,

                            snapshot =
                                snapshot,

                            currentLaneStats =
                                currentLaneStats
                        }
                    );
                }
            }
        );

        app.MapGet(
            "/bridge.png",

            (
                HttpContext context
            ) =>
            {
                var channel =
                    context.Request.Query[
                        "channel"
                    ].ToString();

                var session =
                    context.Request.Query[
                        "session"
                    ].ToString();

                var hasServiceSessionOrdinal =
                    long.TryParse(
                        session,
                        out var serviceSessionOrdinal
                    ) &&
                    serviceSessionOrdinal > 0;

                if (
                    !int.TryParse(
                        context.Request.Query[
                            "chunk"
                        ],
                        out var chunkIndex
                    )
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "chunk parameter must be " +
                                "an integer."
                        }
                    );
                }

                DeadlockLaneAdvisorRosterRequest?
                    laneAdvisorRequest =
                        null;

                var laneAdvisorResultRequest =
                    false;

                var laneAdvisorResultVersion =
                    0L;

                CurrentMatchPlayerHeroRequest[]
                    statsRequests =
                        Array.Empty<
                            CurrentMatchPlayerHeroRequest
                        >();

                CurrentMatchPlayerRankRequest[]
                    rankRequests =
                        Array.Empty<
                            CurrentMatchPlayerRankRequest
                        >();

                PlayerHeroReactionWriteRequest
                    reactionWriteRequest =
                        default;

                uint[]
                    heroDamageAccountIds =
                        Array.Empty<uint>();

                BridgeServiceStatusReportRequest
                    serviceStatusReportRequest =
                        default;

                var currentMatchResultWon =
                    false;

                var currentMatchResultObservedAtUnixMs =
                    0L;

                if (
                    channel ==
                    BridgeServiceStatusReportService
                        .Channel
                )
                {
                    if (!hasServiceSessionOrdinal)
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,

                                error =
                                    "service-status requires a positive " +
                                    "numeric session."
                            }
                        );
                    }

                    if (
                        !BridgeServiceStatusReportService
                            .TryParse(
                                context.Request.Query,
                                out serviceStatusReportRequest,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    CurrentMatchHeroDamagePayloadService
                        .Channel
                )
                {
                    if (
                        !CurrentMatchHeroDamagePayloadService
                            .TryParseQuery(
                                context.Request.Query,
                                out heroDamageAccountIds,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    DeadlockLaneAdvisorService
                        .Channel
                )
                {
                    var laneAdvisorMode =
                        context.Request.Query[
                            "mode"
                        ].ToString();

                    if (
                        laneAdvisorMode ==
                            "result"
                    )
                    {
                        laneAdvisorResultRequest =
                            true;

                        if (
                            !long.TryParse(
                                context.Request.Query[
                                    "version"
                                ],
                                out laneAdvisorResultVersion
                            ) ||
                            laneAdvisorResultVersion <=
                                0
                        )
                        {
                            return Results.BadRequest(
                                new
                                {
                                    ok = false,

                                    error =
                                        "Invalid Lane Advisor result version."
                                }
                            );
                        }
                    }
                    else if (
                        !String.IsNullOrWhiteSpace(
                            laneAdvisorMode
                        )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,

                                error =
                                    "Unknown Lane Advisor mode."
                            }
                        );
                    }
                    else if (
                        !DeadlockLaneAdvisorRosterQueryParser
                            .TryParse(
                                context.Request.Query,
                                out laneAdvisorRequest,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    CurrentMatchPlayerStatsService
                        .Channel
                )
                {
                    if (
                        !CurrentMatchPlayerStatsQueryParser
                            .TryParse(
                                context.Request.Query,
                                out statsRequests,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    PlayerHeroReactionWriteService
                        .Channel
                )
                {
                    if (
                        !PlayerHeroReactionWriteQueryParser
                            .TryParse(
                                context.Request.Query,
                                out reactionWriteRequest,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    CurrentMatchPlayerRanksService
                        .Channel
                )
                {
                    if (
                        !CurrentMatchPlayerRanksQueryParser
                            .TryParse(
                                context.Request.Query,
                                out rankRequests,
                                out var parseError
                            )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,
                                error = parseError
                            }
                        );
                    }
                }
                else if (
                    channel ==
                    CurrentMatchResultChannel
                )
                {
                    var rawWon =
                        context.Request.Query[
                            "won"
                        ].ToString();

                    if (
                        String.Equals(
                            rawWon,
                            "1",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        currentMatchResultWon =
                            true;
                    }
                    else if (
                        !String.Equals(
                            rawWon,
                            "0",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,

                                error =
                                    "current-match-result requires " +
                                    "won=1 or won=0."
                            }
                        );
                    }

                    var rawObservedAtUnixMs =
                        context.Request.Query[
                            "observedAtUnixMs"
                        ].ToString();

                    if (
                        !long.TryParse(
                            rawObservedAtUnixMs,
                            System.Globalization
                                .NumberStyles.None,
                            System.Globalization
                                .CultureInfo.InvariantCulture,
                            out currentMatchResultObservedAtUnixMs
                        )
                    )
                    {
                        return Results.BadRequest(
                            new
                            {
                                ok = false,

                                error =
                                    "current-match-result requires a " +
                                    "long observedAtUnixMs value."
                            }
                        );
                    }
                }

                return transport.CreateChunkResult(
                    channel,
                    session,
                    chunkIndex,

                    () =>
                    {
                        if (
                            channel ==
                            BridgeModuleSettingsTransport
                                .Channel
                        )
                        {
                            return BridgeModuleSettingsTransport
                                .BuildPacket(
                                    BridgeModuleSettingsPersistence
                                        .Load()
                                );
                        }

                        if (
                            channel ==
                            BridgeServiceStatusReportService
                                .Channel
                        )
                        {
                            return BridgeServiceStatusReportService
                                .BuildPacket(
                                    serviceStatusStore,
                                    serviceStatusReportRequest,
                                    serviceSessionOrdinal
                                );
                        }

                        if (
                            channel ==
                            CurrentMatchHeroDamagePayloadService
                                .Channel
                        )
                        {
                            lock (currentMatchLifecycleGate)
                            {
                                try
                                {
                                    if (!IsHeroDamageModuleEnabled())
                                    {
                                        currentMatchContextService
                                            .DisableHeroDamageForCurrentMatch();
                                    }

                                    var matchSnapshot =
                                        currentMatchContextService
                                            .GetSnapshot();

                                    var packet =
                                        CurrentMatchHeroDamagePayloadService
                                            .BuildPacket(
                                                heroDamageAccountIds,
                                                matchSnapshot.LiveDamage
                                            );

                                    if (
                                        matchSnapshot
                                            .HeroDamageAllowedForMatch
                                    )
                                    {
                                        serviceStatusStore.SetState(
                                            BridgeServiceKind
                                                .HeroDamage,

                                            GetHeroDamageServiceState(
                                                matchSnapshot
                                            )
                                        );
                                    }
                                    else
                                    {
                                        serviceStatusStore.Reset(
                                            BridgeServiceKind
                                                .HeroDamage
                                        );
                                    }

                                    return packet;
                                }
                                catch
                                {
                                    serviceStatusStore.SetState(
                                        BridgeServiceKind
                                            .HeroDamage,

                                        BridgeServiceState
                                            .Error
                                    );

                                    throw;
                                }
                            }
                        }

                        if (
                            channel ==
                            DeadlockLaneAdvisorService
                                .Channel
                        )
                        {
                            return ExecuteServiceRequest(
                                serviceStatusStore,
                                BridgeServiceKind.Adviser,
                                hasServiceSessionOrdinal
                                    ? serviceSessionOrdinal
                                    : 0,

                                () =>
                                {
                                    if (laneAdvisorResultRequest)
                                    {
                                        var packet =
                                            laneAdvisorService
                                                .BuildResultPacket(
                                                    laneAdvisorResultVersion,
                                                    out var resultState
                                                );

                                        var serviceState =
                                            resultState switch
                                            {
                                                DeadlockLaneAdvisorResultState
                                                    .Pending =>
                                                        BridgeServiceState
                                                            .InProgress,

                                                DeadlockLaneAdvisorResultState
                                                    .Ready =>
                                                        BridgeServiceState
                                                            .Completed,

                                                DeadlockLaneAdvisorResultState
                                                    .Failed =>
                                                        BridgeServiceState
                                                            .Error,

                                                _ =>
                                                    throw new InvalidOperationException(
                                                        "Unknown Lane Advisor result state."
                                                    )
                                            };

                                        return (
                                            packet,
                                            serviceState
                                        );
                                    }

                                    var includeRank =
                                        BridgeModuleSettingsPersistence
                                            .Load()
                                            .IsEnabled(
                                                BridgeServiceKind
                                                    .Rank
                                            );

                                    lock (currentMatchLifecycleGate)
                                    {
                                        matchPlayerDetailsService
                                            .StartForRoster(
                                                laneAdvisorRequest!,
                                                ownAccountId,
                                                includeRank
                                            );

                                        laneAdvisorService
                                            .StartForRoster(
                                                laneAdvisorRequest!
                                            );
                                    }

                                    var acknowledgment =
                                        BridgeProtocol
                                            .CreatePacket(
                                                BridgeMessageType
                                                    .LaneAdvisorRosterAck,

                                                Array.Empty<byte>()
                                            );

                                    return (
                                        acknowledgment,
                                        BridgeServiceState
                                            .InProgress
                                    );
                                }
                            );
                        }

                        if (
                            channel ==
                            CurrentMatchPlayerStatsService
                                .Channel
                        )
                        {
                            return ExecuteServiceRequest(
                                serviceStatusStore,
                                BridgeServiceKind.Winrate,
                                hasServiceSessionOrdinal
                                    ? serviceSessionOrdinal
                                    : 0,

                                () =>
                                {
                                    var includeRank =
                                        BridgeModuleSettingsPersistence
                                            .Load()
                                            .IsEnabled(
                                                BridgeServiceKind
                                                    .Rank
                                            );

                                    lock (currentMatchLifecycleGate)
                                    {
                                        matchPlayerDetailsService
                                            .StartForRequests(
                                                statsRequests,
                                                includeRank
                                            );
                                    }

                                    var packet =
                                        currentMatchPlayerStatsService
                                            .BuildPacketAsync(
                                                statsRequests,
                                                context.RequestAborted
                                            )
                                            .GetAwaiter()
                                            .GetResult();

                                    return (
                                        packet,
                                        BridgeServiceState
                                            .Completed
                                    );
                                }
                            );
                        }

                        if (
                            channel ==
                            PlayerHeroReactionWriteService
                                .Channel
                        )
                        {
                            return playerHeroReactionWriteService
                                .BuildPacketAsync(
                                    reactionWriteRequest,
                                    context.RequestAborted
                                )
                                .GetAwaiter()
                                .GetResult();
                        }

                        if (
                            channel ==
                            CurrentMatchPlayerRanksService
                                .Channel
                        )
                        {
                            CurrentMatchPlayerRanksSnapshot?
                                completedRankSnapshot =
                                    null;

                            return ExecuteServiceRequest(
                                serviceStatusStore,
                                BridgeServiceKind.Rank,
                                hasServiceSessionOrdinal
                                    ? serviceSessionOrdinal
                                    : 0,

                                () =>
                                {
                                    var result =
                                        currentMatchPlayerRanksService
                                            .BuildPacketResultAsync(
                                                rankRequests,
                                                context.RequestAborted
                                            )
                                            .GetAwaiter()
                                            .GetResult();

                                    completedRankSnapshot =
                                        result.Snapshot;

                                    return (
                                        result.Packet,

                                        result.HasApiErrors
                                            ? BridgeServiceState
                                                .Error
                                            : BridgeServiceState
                                                .Completed
                                    );
                                },

                                () =>
                                {
                                    if (
                                        completedRankSnapshot is
                                            not null
                                    )
                                    {
                                        matchPlayerDetailsService
                                            .ApplyRankSnapshot(
                                                completedRankSnapshot,
                                                BridgeModuleSettingsPersistence
                                                    .Load()
                                                    .IsEnabled(
                                                        BridgeServiceKind
                                                            .Rank
                                                    )
                                            );
                                    }
                                }
                            );
                        }

                        if (
                            channel ==
                            CurrentMatchResultChannel
                        )
                        {
                            lock (currentMatchLifecycleGate)
                            {
                                var snapshot =
                                    currentMatchContextService
                                        .GetSnapshot();

                                var matchObservedAtUtc =
                                    snapshot.MatchObservedAtUtc;

                                if (
                                    !MatchHistoryStore
                                        .IsPlausibleMatchId(
                                            snapshot.MatchId
                                        ) ||
                                    !matchObservedAtUtc.HasValue
                                )
                                {
                                    Log(
                                        "Current match result ignored: " +
                                        "no plausible current match."
                                    );
                                }
                                else
                                {
                                    DateTimeOffset
                                        observedAtUtc;

                                    var timestampValid =
                                        true;

                                    try
                                    {
                                        observedAtUtc =
                                            DateTimeOffset
                                                .FromUnixTimeMilliseconds(
                                                    currentMatchResultObservedAtUnixMs
                                                );
                                    }
                                    catch (
                                        ArgumentOutOfRangeException
                                    )
                                    {
                                        observedAtUtc =
                                            default;

                                        timestampValid =
                                            false;
                                    }

                                    var nowUtc =
                                        DateTimeOffset.UtcNow;

                                    if (
                                        timestampValid &&
                                        (
                                            observedAtUtc >
                                                nowUtc.AddSeconds(5) ||
                                            observedAtUtc <
                                                nowUtc.AddMinutes(-1) ||
                                            observedAtUtc <
                                                matchObservedAtUtc
                                                    .Value
                                        )
                                    )
                                    {
                                        timestampValid =
                                            false;
                                    }

                                    if (!timestampValid)
                                    {
                                        Log(
                                            "Current match result ignored: " +
                                            "invalid or stale observation" +
                                            " | matchId=" +
                                            snapshot.MatchId +
                                            " | observedAtUnixMs=" +
                                            currentMatchResultObservedAtUnixMs
                                        );
                                    }
                                    else if (
                                        currentMatchContextService
                                            .TrySetLocalPlayerWon(
                                                snapshot.MatchId,
                                                currentMatchResultWon,
                                                out var changed
                                            )
                                    )
                                    {
                                        if (changed)
                                        {
                                            matchHistoryCaptureService
                                                .QueueCurrentSnapshotWhileLocked();

                                            Log(
                                                "Current match result accepted" +
                                                " | matchId=" +
                                                snapshot.MatchId +
                                                " | won=" +
                                                (
                                                    currentMatchResultWon
                                                        ? "1"
                                                        : "0"
                                                )
                                            );
                                        }
                                    }
                                    else
                                    {
                                        Log(
                                            "Current match result ignored: " +
                                            "conflicting first-wins result" +
                                            " | matchId=" +
                                            snapshot.MatchId
                                        );
                                    }
                                }
                            }

                            /*
                             * A parsed report is terminally handled even when
                             * it cannot be bound safely. The bounded Panorama
                             * sender retries transport failures only.
                             */
                            return BridgeProtocol
                                .CreatePacket(
                                    BridgeMessageType
                                        .ServiceStatusAck,

                                    new byte[]
                                    {
                                        1
                                    }
                                );
                        }

                        return payloadService.BuildPacket(
                            channel
                        );
                    }
                );
            }
        );

        app.MapGet(
            "/rank-image.png",

            async (
                HttpContext context
            ) =>
            {
                if (
                    !byte.TryParse(
                        context.Request.Query[
                            "rank"
                        ],
                        out var rank
                    ) ||
                    rank < 1 ||
                    rank > 11
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "rank parameter must be " +
                                "from 1 to 11."
                        }
                    );
                }

                if (
                    !byte.TryParse(
                        context.Request.Query[
                            "subrank"
                        ],
                        out var subrank
                    ) ||
                    subrank < 1 ||
                    subrank > 6
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "subrank parameter must be " +
                                "from 1 to 6."
                        }
                    );
                }

                try
                {
                    var bytes =
                        await rankImageService
                            .GetPngAsync(
                                rank,
                                subrank,
                                context.RequestAborted
                            );

                    return Results.File(
                        bytes,
                        "image/png"
                    );
                }
                catch (
                    DeadlockRankImageException error
                )
                {
                    return Results.Problem(
                        title:
                            "Failed to get the rank image.",

                        detail:
                            error.Message,

                        statusCode:
                            (int)error.StatusCode
                    );
                }
                catch (
                    OperationCanceledException
                )
                {
                    return Results.Problem(
                        title:
                            "Rank image request was canceled.",

                        statusCode:
                            499
                    );
                }
                catch (Exception error)
                {
                    LogException(
                        "Rank image proxy error",
                        error
                    );

                    return Results.Problem(
                        title:
                            "Rank image proxy error.",

                        detail:
                            error.Message,

                        statusCode:
                            StatusCodes
                                .Status502BadGateway
                    );
                }
            }
        );

        app.MapGet(
            "/match-player-details",

            () =>
            {
                lock (currentMatchLifecycleGate)
                {
                    var contextSnapshot =
                        currentMatchContextService
                            .GetSnapshot();

                    var snapshot =
                        matchPlayerDetailsService
                            .GetSnapshotForMatch(
                                contextSnapshot.MatchId
                            );

                    return Results.Json(
                        snapshot
                    );
                }
            }
        );

        app.MapPost(
            "/match-player-ranks-refresh",

            () =>
            {
                if (
                    !BridgeModuleSettingsPersistence
                        .Load()
                        .IsEnabled(
                            BridgeServiceKind.Rank
                        )
                )
                {
                    return Results.Json(
                        new
                        {
                            ok = false,
                            started = false,
                            retry = false,
                            reason = "rank-disabled"
                        }
                    );
                }

                bool started;
                bool retry;

                lock (currentMatchLifecycleGate)
                {
                    var contextSnapshot =
                        currentMatchContextService
                            .GetSnapshot();

                    var detailsSnapshot =
                        matchPlayerDetailsService
                            .GetSnapshotForMatch(
                                contextSnapshot.MatchId
                            );

                    started =
                        matchPlayerDetailsService
                            .RefreshRanksForMatch(
                                contextSnapshot.MatchId
                            );

                    retry =
                        !started &&
                        contextSnapshot.MatchId != 0 &&
                        (
                            String.Equals(
                                detailsSnapshot.Status,
                                "waiting",
                                StringComparison.Ordinal
                            ) ||
                            String.Equals(
                                detailsSnapshot.Status,
                                "loading",
                                StringComparison.Ordinal
                            )
                        );
                }

                return Results.Json(
                    new
                    {
                        ok = true,
                        started,
                        retry
                    }
                );
            }
        );

        app.MapGet(
            "/recent",

            () =>
                Results.Json(
                    payloadService
                        .BuildDiagnosticSnapshot()
                )
        );

        app.MapGet(
            "/lane-advisor-diagnostics",

            (
                HttpContext context
            ) =>
            {
                var after =
                    0L;

                var rawAfter =
                    context.Request.Query[
                        "after"
                    ].ToString();

                if (
                    !String.IsNullOrWhiteSpace(
                        rawAfter
                    ) &&
                    (
                        !long.TryParse(
                            rawAfter,
                            out after
                        ) ||
                        after < 0
                    )
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "after parameter must be " +
                                "a non-negative integer."
                        }
                    );
                }

                return Results.Json(
                    new
                    {
                        ok = true,

                        events =
                            laneAdvisorService
                                .GetDiagnosticEventsAfter(
                                    after
                                )
                    }
                );
            }
        );

        app.MapGet(
            "/steam-party-diagnostics",

            async (
                HttpContext context
            ) =>
            {
                try
                {
                    var currentPlayers =
                        payloadService
                            .GetCurrentMatchPlayers();

                    var snapshot =
                        await partyPresenceService
                            .GetSnapshotAsync(
                                currentPlayers,
                                context.RequestAborted
                            );

                    return Results.Json(
                        snapshot
                    );
                }
                catch (
                    OperationCanceledException
                )
                {
                    return Results.Problem(
                        title:
                            "Steam party diagnostics request was canceled.",

                        statusCode:
                            499
                    );
                }
                catch (Exception error)
                {
                    LogException(
                        "Steam party diagnostics error",
                        error
                    );

                    return Results.Problem(
                        title:
                            "Steam party diagnostics error.",

                        detail:
                            error.Message,

                        statusCode:
                            StatusCodes
                                .Status502BadGateway
                    );
                }
            }
        );

        app.MapGet(
            "/deadlock-api/player-stats-preview",

            async (
                HttpContext context
            ) =>
            {
                if (
                    !CurrentMatchPlayerStatsQueryParser
                        .TryParse(
                            context.Request.Query,
                            out var requests,
                            out var parseError
                        )
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,
                            error = parseError
                        }
                    );
                }

                try
                {
                    var snapshot =
                        await currentMatchPlayerStatsService
                            .GetSnapshotAsync(
                                requests,
                                context.RequestAborted
                            );

                    return Results.Json(
                        new
                        {
                            ok = true,

                            generatedAtUtc =
                                snapshot.GeneratedAtUtc
                                    .ToString("O"),

                            players =
                                snapshot.Players.Select(
                                    player =>
                                        new
                                        {
                                            player.Index,
                                            player.AccountId,
                                            player.InputHeroName,
                                            player.ApiHeroName,

                                            status =
                                                player.Status
                                                    .ToString(),

                                            player.HeroId,
                                            player.MatchesPlayed,
                                            player.Wins,

                                            winRatePercent =
                                                Math.Round(
                                                    player
                                                        .WinRatePercent,
                                                    2
                                                )
                                        }
                                )
                        }
                    );
                }
                catch (
                    DeadlockApiException error
                )
                {
                    LogException(
                        "Deadlock API player-stats error",
                        error
                    );

                    return Results.Json(
                        new
                        {
                            ok = false,

                            statusCode =
                                (int)error.StatusCode,

                            error =
                                error.Message,

                            diagnosticHeaders =
                                error.DiagnosticHeaders
                        },

                        statusCode:
                            (int)error.StatusCode
                    );
                }
                catch (
                    OperationCanceledException
                )
                {
                    return Results.Problem(
                        title:
                            "player-stats request was canceled.",

                        statusCode:
                            499
                    );
                }
                catch (Exception error)
                {
                    LogException(
                        "current-match player-stats error",
                        error
                    );

                    return Results.Problem(
                        title:
                            "current-match player-stats error.",

                        detail:
                            error.Message,

                        statusCode:
                            StatusCodes
                                .Status502BadGateway
                    );
                }
            }
        );

        app.MapGet(
            "/deadlock-api/current-match-hero-stats",

            async (
                HttpContext context
            ) =>
            {
                var currentPlayers =
                    payloadService
                        .GetCurrentMatchPlayers();

                try
                {
                    var stats =
                        await playerStatsService
                            .GetHeroStatsAsync(
                                currentPlayers.Select(
                                    player =>
                                        player.AccountId
                                ),

                                heroIds:
                                    null,

                                context.RequestAborted
                            );

                    var statsByAccountId =
                        stats
                            .GroupBy(
                                row =>
                                    row.AccountId
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
                                        .ToArray()
                            );

                    var players =
                        currentPlayers
                            .Select(
                                player =>
                                {
                                    statsByAccountId
                                        .TryGetValue(
                                            player.AccountId,
                                            out var heroRows
                                        );

                                    heroRows ??=
                                        Array.Empty<
                                            DeadlockHeroStats
                                        >();

                                    return new
                                    {
                                        accountId =
                                            player.AccountId,

                                        steamId64 =
                                            player
                                                .SteamId64
                                                .ToString(),

                                        steamName =
                                            player.PersonaName,

                                        isLocal =
                                            player.IsLocal,

                                        heroStats =
                                            heroRows.Select(
                                                row =>
                                                    new
                                                    {
                                                        accountId =
                                                            row.AccountId,

                                                        heroId =
                                                            row.HeroId,

                                                        matchesPlayed =
                                                            row.MatchesPlayed,

                                                        wins =
                                                            row.Wins,

                                                        winRatePercent =
                                                            Math.Round(
                                                                row
                                                                    .WinRatePercent,
                                                                2
                                                            ),

                                                        lastPlayed =
                                                            row.LastPlayed,

                                                        timePlayedSeconds =
                                                            row.TimePlayed,

                                                        kills =
                                                            row.Kills,

                                                        deaths =
                                                            row.Deaths,

                                                        assists =
                                                            row.Assists,

                                                        killsPerMinute =
                                                            Math.Round(
                                                                row
                                                                    .KillsPerMinute,
                                                                3
                                                            ),

                                                        deathsPerMinute =
                                                            Math.Round(
                                                                row
                                                                    .DeathsPerMinute,
                                                                3
                                                            ),

                                                        assistsPerMinute =
                                                            Math.Round(
                                                                row
                                                                    .AssistsPerMinute,
                                                                3
                                                            ),

                                                        networthPerMinute =
                                                            Math.Round(
                                                                row
                                                                    .NetworthPerMinute,
                                                                2
                                                            ),

                                                        damagePerMinute =
                                                            Math.Round(
                                                                row
                                                                    .DamagePerMinute,
                                                                2
                                                            ),

                                                        accuracyPercent =
                                                            Math.Round(
                                                                row.Accuracy *
                                                                100.0,
                                                                2
                                                            )
                                                    }
                                            )
                                            .ToArray()
                                    };
                                }
                            )
                            .ToArray();

                    return Results.Json(
                        new
                        {
                            ok = true,

                            source =
                                "deadlock-api.com",

                            generatedAtUtc =
                                DateTimeOffset.UtcNow
                                    .ToString("O"),

                            requestedPlayers =
                                currentPlayers.Count,

                            playersWithStats =
                                players.Count(
                                    player =>
                                        player.heroStats.Length >
                                            0
                                ),

                            totalHeroRows =
                                stats.Count,

                            players
                        }
                    );
                }
                catch (
                    DeadlockApiException error
                )
                {
                    LogException(
                        "Deadlock API hero-stats error",
                        error
                    );

                    return Results.Json(
                        new
                        {
                            ok = false,

                            source =
                                "deadlock-api.com",

                            statusCode =
                                (int)error.StatusCode,

                            error =
                                error.Message,

                            diagnosticHeaders =
                                error.DiagnosticHeaders
                        },

                        statusCode:
                            (int)error.StatusCode
                    );
                }
                catch (
                    OperationCanceledException
                )
                {
                    return Results.Problem(
                        title:
                            "Deadlock API request was canceled.",

                        statusCode:
                            499
                    );
                }
                catch (Exception error)
                {
                    LogException(
                        "Deadlock API client error",
                        error
                    );

                    return Results.Problem(
                        title:
                            "Deadlock API client error.",

                        detail:
                            error.Message,

                        statusCode:
                            StatusCodes
                                .Status502BadGateway
                    );
                }
            }
        );

        app.MapDeadlockHeroCatalogEndpoints(
            heroCatalogService
        );

        return app;
    }

    private static byte[] ExecuteServiceRequest(
        BridgeServiceStatusStore statusStore,
        BridgeServiceKind service,
        long sessionOrdinal,
        Func<(
            byte[] Packet,
            BridgeServiceState State
        )> packetFactory,
        Action? acceptedHandler = null
    )
    {
        ArgumentNullException.ThrowIfNull(
            statusStore
        );

        ArgumentNullException.ThrowIfNull(
            packetFactory
        );

        BridgeServiceRequestToken token =
            default;

        var tracksStatus =
            sessionOrdinal > 0 &&
            statusStore.TryBegin(
                service,
                sessionOrdinal,
                out token
            );

        try
        {
            var result =
                packetFactory();

            var accepted =
                sessionOrdinal <= 0;

            if (tracksStatus)
            {
                /*
                 * A newer session may have started while this request was
                 * waiting for an API response. Complete() then returns false,
                 * and the older request is still served without touching the
                 * newer visible state.
                 */
                accepted =
                    statusStore.Complete(
                        token,
                        result.State
                    );
            }

            if (accepted)
            {
                try
                {
                    acceptedHandler?.Invoke();
                }
                catch
                {
                    /*
                     * A supplementary desktop update must not turn a
                     * successfully built Panorama response into an HTTP
                     * failure.
                     */
                }
            }

            return result.Packet;
        }
        catch
        {
            if (tracksStatus)
            {
                statusStore.Complete(
                    token,
                    BridgeServiceState.Error
                );
            }

            throw;
        }
    }

    private static BridgeServiceState
        GetHeroDamageServiceState(
            CurrentMatchContextSnapshot snapshot
        )
    {
        ArgumentNullException.ThrowIfNull(
            snapshot
        );

        var probe =
            snapshot.BroadcastProbe;

        var liveDamage =
            snapshot.LiveDamage;

        if (
            String.Equals(
                probe.Status,
                "error",
                StringComparison.Ordinal
            ) ||
            String.Equals(
                liveDamage.Status,
                "error",
                StringComparison.Ordinal
            ) ||
            (
                String.Equals(
                    liveDamage.Status,
                    "ended",
                    StringComparison.Ordinal
                ) &&
                !liveDamage.ConnectedAtUtc
                    .HasValue
            )
        )
        {
            return BridgeServiceState.Error;
        }

        /*
         * READY from the parser is the first reliable point at which the
         * Valve stream has started. A successfully encoded waiting packet
         * is not completion, and temporary relay reconnects do not erase it.
         */
        if (
            liveDamage.ConnectedAtUtc
                .HasValue
        )
        {
            return BridgeServiceState.Completed;
        }

        return BridgeServiceState.InProgress;
    }

    private static bool IsHeroDamageModuleEnabled()
    {
        return BridgeModuleSettingsPersistence
            .Load()
            .IsEnabled(
                BridgeServiceKind
                    .HeroDamage
            );
    }

    private async Task RunSteamCallbacksAsync(
        CancellationToken cancellationToken
    )
    {
        while (
            !cancellationToken
                .IsCancellationRequested
        )
        {
            lock (_steamGate)
            {
                SteamAPI.RunCallbacks();
            }

            try
            {
                await Task.Delay(
                    100,
                    cancellationToken
                );
            }
            catch (
                OperationCanceledException
            )
            {
                break;
            }
        }
    }

    private async Task WaitForDeadlockReadyAsync(
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            await WaitForProcessAsync(
                DeadlockProcessName,
                cancellationToken
            );

            await Task.Delay(
                TimeSpan.FromSeconds(3),
                cancellationToken
            );

            if (
                IsProcessRunning(
                    DeadlockProcessName
                )
            )
            {
                return;
            }
        }
    }

    private async Task<BridgeRuntimeExitReason>
        WaitForExitReasonAsync(
            CancellationToken cancellationToken
        )
    {
        while (true)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (
                !IsProcessRunning(
                    DeadlockProcessName
                )
            )
            {
                Log(
                    "Deadlock closed. " +
                    "Bridge is shutting down."
                );

                _view.SetDeadlockRunning(
                    false
                );

                return BridgeRuntimeExitReason
                    .DeadlockExited;
            }

            await Task.Delay(
                1000,
                cancellationToken
            );
        }
    }

    private static async Task WaitForProcessAsync(
        string processName,
        CancellationToken cancellationToken
    )
    {
        while (
            !IsProcessRunning(
                processName
            )
        )
        {
            await Task.Delay(
                500,
                cancellationToken
            );
        }
    }

    private static bool IsProcessRunning(
        string processName
    )
    {
        var processes =
            Process.GetProcessesByName(
                processName
            );

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (
                var process in processes
            )
            {
                process.Dispose();
            }
        }
    }

    private void Log(
        string message
    )
    {
        _view.AppendLog(
            message
        );
    }

    private void LogError(
        string message
    )
    {
        _view.AppendLog(
            "ERROR: " +
            message
        );
    }

    private void LogException(
        string title,
        Exception error
    )
    {
        _view.AppendLog(
            "ERROR: " +
            title +
            Environment.NewLine +
            error
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed =
            true;

        RequestStop();

        _stopCancellation.Dispose();
    }
}
