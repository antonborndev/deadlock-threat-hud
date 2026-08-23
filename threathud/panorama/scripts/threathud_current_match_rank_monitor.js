var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var REQUEST_RETRY_DELAY =
        1.0;

    var MAX_REQUEST_ATTEMPTS =
        20;

    /*
     * Only render() is repeated.
     *
     * Fetching the rank result through Bridge
     * is not started again.
     */
    var RENDER_RETRY_DELAY =
        1.0;

    var MAX_RENDER_ATTEMPTS =
        10;

    /*
     * Synthetic sandbox ranks.
     */
    var SANDBOX_RANKS = [
        { rank: 1, subrank: 1 },
        { rank: 2, subrank: 2 },
        { rank: 3, subrank: 3 },
        { rank: 4, subrank: 4 },
        { rank: 5, subrank: 5 },
        { rank: 6, subrank: 6 },
        { rank: 7, subrank: 1 },
        { rank: 8, subrank: 2 },
        { rank: 9, subrank: 3 },
        { rank: 10, subrank: 4 },
        { rank: 11, subrank: 5 },
        { rank: 11, subrank: 6 }
    ];

    function CurrentMatchRankMonitor(
        playerRanksClient,
        rankOverlay,
        logger,
        serviceStatusClient
    ) {
        this._playerRanksClient =
            playerRanksClient;

        this._rankOverlay =
            rankOverlay;

        this._log =
            typeof logger ===
                'function'
                    ? logger
                    : function () {};

        this._serviceStatusClient =
            serviceStatusClient &&
            typeof serviceStatusClient.report ===
                'function'
                    ? serviceStatusClient
                    : null;

        this._sandboxServiceStatusActive =
            false;

        /*
         * The context comes from CurrentMatchStatsMonitor
         * only after winrate has been rendered successfully.
         *
         * Therefore, a separate stable-window snapshot
         * and a second comparison of the same roster are no longer
         * needed.
         */
        this._rosterContext =
            null;

        this._requestRunning =
            false;

        this._completed =
            false;

        this._requestAttemptCount =
            0;

        this._generation =
            0;

        this._scheduleVersion =
            0;

        /*
         * The rank result has already been received and is waiting
         * only for a successful overlay.render().
         */
        this._pendingRender =
            null;

        this._renderAttemptCount =
            0;

        /*
         * The last successfully rendered
         * rank result.
         */
        this._lastResult =
            null;

        var self =
            this;

        this._rosterContextChangedHandler =
            function (context) {
                self._acceptRosterContext(
                    context
                );
            };
    }

    CurrentMatchRankMonitor.prototype
        .getRosterContextChangedHandler =
        function () {
            return this
                ._rosterContextChangedHandler;
        };

    CurrentMatchRankMonitor.prototype
        .getLastResult =
        function () {
            return this._lastResult;
        };

    /*
     * Single request/render state reset.
     *
     * stop(), context replacement, and abort
     * use one shared location instead of several
     * identical sets of assignments.
     */
    CurrentMatchRankMonitor.prototype
        ._resetRequestState =
        function () {
            this._cancelSandboxServiceStatus();

            this._requestRunning =
                false;

            this._completed =
                false;

            this._requestAttemptCount =
                0;

            this._pendingRender =
                null;

            this._renderAttemptCount =
                0;

            this._lastResult =
                null;
        };

    /*
     * Creates a new generation for the specified
     * roster context and invalidates the previous one.
     * async request/render.
     */
    CurrentMatchRankMonitor.prototype
        ._replaceContext =
        function (
            context,
            reason
        ) {
            var hiddenCount =
                this._clearOverlay(
                    reason
                );

            this._rosterContext =
                context
                    ? {
                        mode:
                            context.mode,

                        matches:
                            context.matches.slice(
                                0
                            )
                    }
                    : null;

            this._resetRequestState();

            this._generation +=
                1;

            this._scheduleVersion +=
                1;

            return hiddenCount;
        };

    CurrentMatchRankMonitor.prototype.stop =
        function (reason) {
            var hadState =
                !!this._rosterContext ||
                this._requestRunning ||
                this._completed ||
                !!this._pendingRender ||
                !!this._lastResult;

            var hiddenCount =
                this._replaceContext(
                    null,
                    reason ||
                        'roster-invalidated'
                );

            if (
                hadState ||
                hiddenCount > 0
            ) {
                this._log(
                    'CurrentMatchRankMonitor: STOP' +
                    ' | reason=' +
                    String(
                        reason ||
                        'roster-invalidated'
                    ) +
                    ' | hidden=' +
                    hiddenCount
                );
            }

            return (
                hadState ||
                hiddenCount > 0
            );
        };

    /*
     * CurrentMatchStatsMonitor invokes this
     * callback only after successful rendering
     * winrate.
     */
    CurrentMatchRankMonitor.prototype
        ._acceptRosterContext =
        function (context) {
            if (!context) {
                this.stop(
                    'roster-context-cleared'
                );

                return true;
            }

            if (
                !this._isValidRosterContext(
                    context
                )
            ) {
                this._log(
                    'CurrentMatchRankMonitor: CONTEXT REJECTED' +
                    ' | reason=invalid-context'
                );

                this.stop(
                    'invalid-roster-context'
                );

                return false;
            }

            if (
                this._rosterContext &&
                this._sameRosterContext(
                    this._rosterContext,
                    context
                )
            ) {
                this._log(
                    'CurrentMatchRankMonitor: CONTEXT UNCHANGED' +
                    ' | mode=' +
                    context.mode
                );

                this._tryStart(
                    this._generation
                );

                return true;
            }

            this._replaceContext(
                context,
                'roster-context-replaced'
            );

            this._log(
                'CurrentMatchRankMonitor: CONTEXT READY' +
                ' | mode=' +
                    this._rosterContext.mode +
                ' | players=' +
                    this._rosterContext
                        .matches.length +
                ' | afterStatsRender=true'
            );

            this._tryStart(
                this._generation
            );

            return true;
        };

    /*
     * The context has already passed the stats render gate,
     * so the rank workflow can start
     * immediately after receiving it.
     */
    CurrentMatchRankMonitor.prototype
        ._tryStart =
        function (generation) {
            if (
                generation !==
                    this._generation ||
                this._completed ||
                this._requestRunning ||
                !!this._pendingRender ||
                !this._rosterContext
            ) {
                return false;
            }

            this._log(
                'CurrentMatchRankMonitor: WORKFLOW READY' +
                ' | mode=' +
                    this._rosterContext.mode +
                ' | players=' +
                    this._rosterContext
                        .matches.length
            );

            if (
                this._rosterContext.mode ===
                    'sandbox'
            ) {
                this._beginSandboxServiceStatus();

                this._finishWithResult(
                    generation,
                    this._buildSandboxResult(
                        this._rosterContext
                            .matches
                    ),
                    'sandbox'
                );

                return true;
            }

            this._requestRealRanks(
                generation
            );

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._requestRealRanks =
        function (generation) {
            var self;
            var callbackInvoked;
            var started;

            if (
                generation !==
                    this._generation ||
                this._completed ||
                this._requestRunning ||
                !!this._pendingRender ||
                !this._rosterContext ||
                this._rosterContext.mode !==
                    'real'
            ) {
                return false;
            }

            if (
                this._requestAttemptCount >=
                    MAX_REQUEST_ATTEMPTS
            ) {
                this._abortCurrent(
                    generation,
                    'max-request-attempts'
                );

                return false;
            }

            this._requestRunning =
                true;

            this._requestAttemptCount += 1;

            this._log(
                'CurrentMatchRankMonitor: REQUEST' +
                ' | attempt=' +
                    this._requestAttemptCount +
                ' | players=' +
                    this._rosterContext
                        .matches.length
            );

            self =
                this;

            callbackInvoked =
                false;

            started =
                this._playerRanksClient
                    .getForMatches(
                        this._rosterContext
                            .matches,

                        function (
                            error,
                            result
                        ) {
                            callbackInvoked =
                                true;

                            if (
                                generation !==
                                    self._generation ||
                                self._completed
                            ) {
                                return;
                            }

                            self._requestRunning =
                                false;

                            if (error) {
                                self._log(
                                    'CurrentMatchRankMonitor: REQUEST ERROR' +
                                    ' | code=' +
                                        String(
                                            error.code ||
                                            'unknown-error'
                                        ) +
                                    ' | message=' +
                                        String(
                                            error.message ||
                                            ''
                                        )
                                );

                                self._scheduleRetry(
                                    'request',
                                    REQUEST_RETRY_DELAY,
                                    generation,
                                    error.code ||
                                        'request-error'
                                );

                                return;
                            }

                            self._finishWithResult(
                                generation,
                                result,
                                'real'
                            );
                        }
                    );

            if (
                !started &&
                !callbackInvoked
            ) {
                this._requestRunning =
                    false;

                this._scheduleRetry(
                    'request',
                    REQUEST_RETRY_DELAY,
                    generation,
                    'client-not-started'
                );
            }

            return started;
        };

    /*
     * Single scheduler for request/render retries.
     *
     * Shared generation/version protection is located
     * in one place; only the limit,
     * additional state, and target callback differ.
     */
    CurrentMatchRankMonitor.prototype
        ._scheduleRetry =
        function (
            kind,
            delay,
            generation,
            reason
        ) {
            var renderRetry =
                kind === 'render';

            if (
                generation !== this._generation ||
                this._completed ||
                (
                    renderRetry &&
                    !this._pendingRender
                )
            ) {
                return false;
            }

            if (
                renderRetry
                    ? this._renderAttemptCount >=
                        MAX_RENDER_ATTEMPTS
                    : this._requestAttemptCount >=
                        MAX_REQUEST_ATTEMPTS
            ) {
                this._abortCurrent(
                    generation,
                    renderRetry
                        ? 'max-render-attempts'
                        : 'max-request-attempts'
                );

                return false;
            }

            this._scheduleVersion += 1;

            var scheduleVersion =
                this._scheduleVersion;

            var self =
                this;

            this._log(
                renderRetry
                    ? (
                        'CurrentMatchRankMonitor: RENDER SCHEDULE' +
                        ' | delay=' + delay +
                        ' | nextAttempt=' +
                        (
                            this._renderAttemptCount +
                            1
                        )
                    )
                    : (
                        'CurrentMatchRankMonitor: RETRY SCHEDULE' +
                        ' | delay=' + delay +
                        ' | reason=' +
                        String(reason || '')
                    )
            );

            $.Schedule(
                delay,

                function () {
                    if (
                        generation !== self._generation ||
                        scheduleVersion !== self._scheduleVersion ||
                        self._completed ||
                        (
                            renderRetry &&
                            !self._pendingRender
                        )
                    ) {
                        return;
                    }

                    if (renderRetry) {
                        self._attemptPendingRender(
                            generation
                        );
                    } else {
                        self._tryStart(
                            generation
                        );
                    }
                }
            );

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._buildSandboxResult =
        function (matches) {
            var players =
                [];

            for (
                var index = 0;
                index < matches.length;
                index += 1
            ) {
                var match =
                    matches[index];

                var synthetic =
                    SANDBOX_RANKS[index];

                players.push({
                    index:
                        index,

                    rosterIndex:
                        match.rosterIndex,

                    team:
                        match.team,

                    teamIndex:
                        match.teamIndex,

                    playerName:
                        match.playerName,

                    personaName:
                        match.status ===
                            'resolved'
                                ? match.personaName
                                : match.playerName,

                    heroName:
                        match.heroName,

                    accountId:
                        match.status ===
                            'resolved'
                                ? match.accountId
                                : 0,

                    accountIdText:
                        match.status ===
                            'resolved'
                                ? match.accountIdText
                                : '',

                    isLocal:
                        match.isLocal,

                    panel:
                        match.panel,

                    playerNameLabel:
                        match.playerNameLabel,

                    heroNameLabel:
                        match.heroNameLabel,

                    statusCode:
                        0,

                    status:
                        'ok',

                    rank:
                        synthetic.rank,

                    subrank:
                        synthetic.subrank,

                    badge:
                        synthetic.rank *
                            10 +
                        synthetic.subrank,

                    isSandboxPreview:
                        true
                });
            }

            return {
                count:
                    players.length,

                okCount:
                    players.length,

                unrankedCount:
                    0,

                protectedCount:
                    0,

                notFoundCount:
                    0,

                apiErrorCount:
                    0,

                players:
                    players
            };
        };

    CurrentMatchRankMonitor.prototype
        ._finishWithResult =
        function (
            generation,
            result,
            mode
        ) {
            if (
                generation !==
                    this._generation ||
                this._completed
            ) {
                return false;
            }

            if (
                !result ||
                !result.players ||
                result.players.length !==
                    EXPECTED_PLAYERS
            ) {
                this._abortCurrent(
                    generation,
                    'invalid-rank-result'
                );

                return false;
            }

            this._requestRunning =
                false;

            this._pendingRender = {
                result:
                    result,

                mode:
                    mode
            };

            this._renderAttemptCount =
                0;

            this._lastResult =
                null;

            this._scheduleVersion += 1;

            this._logResult(
                result,
                mode
            );

            this._attemptPendingRender(
                generation
            );

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._attemptPendingRender =
        function (generation) {
            if (
                generation !==
                    this._generation ||
                this._completed ||
                !this._pendingRender
            ) {
                return false;
            }

            if (
                !this._rankOverlay ||
                typeof this._rankOverlay.render !==
                    'function'
            ) {
                this._abortCurrent(
                    generation,
                    'rank-overlay-unavailable'
                );

                return false;
            }

            if (
                this._renderAttemptCount >=
                    MAX_RENDER_ATTEMPTS
            ) {
                this._abortCurrent(
                    generation,
                    'max-render-attempts'
                );

                return false;
            }

            this._renderAttemptCount += 1;

            var rendered =
                false;

            try {
                rendered =
                    this._rankOverlay.render(
                        this._pendingRender
                            .result
                            .players
                    );
            } catch (renderError) {
                this._log(
                    'CurrentMatchRankMonitor: OVERLAY ERROR' +
                    ' | attempt=' +
                        this._renderAttemptCount +
                    ' | error=' +
                        String(renderError)
                );

                rendered =
                    false;
            }

            this._log(
                'CurrentMatchRankMonitor: OVERLAY' +
                ' | rendered=' +
                    rendered +
                ' | attempt=' +
                    this._renderAttemptCount +
                ' | mode=' +
                    this._pendingRender.mode
            );

            if (!rendered) {
                this._scheduleRetry(
                    'render',
                    RENDER_RETRY_DELAY,
                    generation
                );

                return false;
            }

            var completedMode =
                this._pendingRender.mode;

            this._lastResult =
                this._pendingRender.result;

            this._pendingRender =
                null;

            this._completed =
                true;

            if (
                completedMode ===
                    'sandbox'
            ) {
                this._completeSandboxServiceStatus();
            }

            this._scheduleVersion += 1;

            this._log(
                'CurrentMatchRankMonitor: DONE' +
                ' | mode=' +
                    completedMode +
                ' | requestAttempts=' +
                    this._requestAttemptCount +
                ' | renderAttempts=' +
                    this._renderAttemptCount
            );

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._abortCurrent =
        function (
            generation,
            reason
        ) {
            if (
                generation !==
                    this._generation
            ) {
                return false;
            }

            var requestAttempts =
                this._requestAttemptCount;

            var renderAttempts =
                this._renderAttemptCount;

            this._failSandboxServiceStatus();

            this._resetRequestState();

            this._scheduleVersion += 1;

            this._clearOverlay(
                reason ||
                    'workflow-aborted'
            );

            this._log(
                'CurrentMatchRankMonitor: ABORT' +
                ' | reason=' +
                    String(reason || '') +
                ' | requestAttempts=' +
                    requestAttempts +
                ' | renderAttempts=' +
                    renderAttempts
            );

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._beginSandboxServiceStatus =
        function () {
            this._sandboxServiceStatusActive =
                true;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'rank',
                    'in-progress'
                );
            }
        };

    CurrentMatchRankMonitor.prototype
        ._completeSandboxServiceStatus =
        function () {
            if (!this._sandboxServiceStatusActive) {
                return;
            }

            this._sandboxServiceStatusActive =
                false;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'rank',
                    'completed'
                );
            }
        };

    CurrentMatchRankMonitor.prototype
        ._failSandboxServiceStatus =
        function () {
            if (!this._sandboxServiceStatusActive) {
                return;
            }

            this._sandboxServiceStatusActive =
                false;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'rank',
                    'error'
                );
            }
        };

    CurrentMatchRankMonitor.prototype
        ._cancelSandboxServiceStatus =
        function () {
            this._sandboxServiceStatusActive =
                false;
        };

    CurrentMatchRankMonitor.prototype
        ._sameRosterContext =
        function (
            left,
            right
        ) {
            if (
                !this._isValidRosterContext(
                    left
                ) ||
                !this._isValidRosterContext(
                    right
                ) ||
                left.mode !==
                    right.mode
            ) {
                return false;
            }

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var leftMatch =
                    left.matches[index];

                var rightMatch =
                    right.matches[index];

                if (
                    leftMatch.rosterIndex !==
                        rightMatch.rosterIndex ||
                    leftMatch.panel !==
                        rightMatch.panel ||
                    leftMatch.playerName !==
                        rightMatch.playerName ||
                    leftMatch.heroName !==
                        rightMatch.heroName ||
                    leftMatch.team !==
                        rightMatch.team ||
                    leftMatch.teamIndex !==
                        rightMatch.teamIndex ||
                    leftMatch.playerNameLabel !==
                        rightMatch.playerNameLabel ||
                    leftMatch.heroNameLabel !==
                        rightMatch.heroNameLabel ||
                    leftMatch.status !==
                        rightMatch.status ||
                    leftMatch.accountIdText !==
                        rightMatch.accountIdText ||
                    leftMatch.isLocal !==
                        rightMatch.isLocal
                ) {
                    return false;
                }
            }

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._clearOverlay =
        function (reason) {
            if (
                !this._rankOverlay ||
                typeof this._rankOverlay.clear !==
                    'function'
            ) {
                return 0;
            }

            try {
                var hiddenCount =
                    this._rankOverlay.clear();

                return (
                    typeof hiddenCount ===
                        'number'
                            ? hiddenCount
                            : 0
                );
            } catch (clearError) {
                this._log(
                    'CurrentMatchRankMonitor: OVERLAY CLEAR ERROR' +
                    ' | reason=' +
                        String(reason || '') +
                    ' | error=' +
                        String(clearError)
                );

                return 0;
            }
        };
    
    CurrentMatchRankMonitor.prototype
        ._isValidRosterContext =
        function (context) {
            if (
                !context ||
                (
                    context.mode !==
                        'real' &&
                    context.mode !==
                        'sandbox'
                ) ||
                !context.matches ||
                context.matches.length !==
                    EXPECTED_PLAYERS
            ) {
                return false;
            }

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                if (!context.matches[index]) {
                    return false;
                }
            }

            return true;
        };

    CurrentMatchRankMonitor.prototype
        ._logResult =
        function (
            result,
            mode
        ) {
            this._log(
                'Current match ranks RESULT' +
                ' | mode=' + mode +
                ' | players=' +
                    result.count +
                ' | ok=' +
                    result.okCount +
                ' | unranked=' +
                    result.unrankedCount +
                ' | protected=' +
                    result.protectedCount +
                ' | notFound=' +
                    result.notFoundCount +
                ' | apiError=' +
                    result.apiErrorCount
            );

            for (
                var index = 0;
                index < result.players.length;
                index += 1
            ) {
                var player =
                    result.players[index];

                this._log(
                    'Rank [' +
                        player.rosterIndex +
                    ']' +
                    ' | team=' +
                        player.team +
                    ' | player=' +
                        player.playerName +
                    ' | hero=' +
                        player.heroName +
                    ' | accountID=' +
                        player.accountIdText +
                    ' | status=' +
                        player.status +
                    ' | rank=' +
                        player.rank +
                    ' | subrank=' +
                        player.subrank +
                    ' | badge=' +
                        player.badge +
                    ' | local=' +
                        player.isLocal +
                    ' | sandbox=' +
                        player.isSandboxPreview
                );
            }
        };

    ThreatHud.CurrentMatchRankMonitor =
        CurrentMatchRankMonitor;

})(ThreatHud);
