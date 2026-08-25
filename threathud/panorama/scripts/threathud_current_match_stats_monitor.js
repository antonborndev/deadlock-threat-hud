var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var AUTOMATIC_START_DELAY = 0.05;
    var AUTOMATIC_ROSTER_RETRY_DELAY = 0.5;
    var MANUAL_ROSTER_RETRY_DELAY = 7.0;

    /*
     * While pending/ambiguous identities remain,
     * Panorama repeatedly requests from Bridge
     * a fresh Steam Recent snapshot.
     */
    var IDENTITY_REFRESH_DELAY = 10.0;

    /*
     * Retry after a temporary transport,
     * Bridge, API, or internal client state error.
     */
    var TRANSIENT_ERROR_RETRY_DELAY =
        15.0;

    var RENDER_RETRY_DELAY = 2.0;
    var MAX_ATTEMPTS = 40;

    function CurrentMatchStatsMonitor(
        matchRosterMatcher,
        playerStatsClient,
        statsOverlay,
        logger,
        onRosterContextChanged,
        laneAdvisorClient,
        serviceStatusClient
    ) {
        this._matcher = matchRosterMatcher;
        this._playerStatsClient = playerStatsClient;
        this._statsOverlay = statsOverlay;
        this._log = typeof logger === 'function' ? logger : function () {};

        /*
         * Lane Advisor receives the ordered roster
         * directly after the matcher.
         *
         * It does not depend on player stats / rank.
         */
        this._laneAdvisorClient =
            laneAdvisorClient &&
            typeof laneAdvisorClient.startForMatches ===
                'function'
                    ? laneAdvisorClient
                    : null;

        this._serviceStatusClient =
            serviceStatusClient &&
            typeof serviceStatusClient.report ===
                'function'
                    ? serviceStatusClient
                    : null;

        this._sandboxServiceStatusActive =
            false;

        /*
         * The context is published only after the stats
         * presentation adapter accepted all 12 players.
         * The adapter still accepts and forwards context
         * while the Winrate visuals are disabled, so Rank
         * and Reaction do not depend on Winrate visibility.
         */
        this._onRosterContextChanged =
            typeof onRosterContextChanged ===
                'function'
                    ? onRosterContextChanged
                    : function () {};

        this._currentRosterContext =
            null;

        this._started = false;
        this._runMode = null;
        this._requestRunning = false;
        this._completed = false;
        this._attemptCount = 0;
        this._generation = 0;
        this._scheduleVersion = 0;

        /*
         * Ready stats waiting
         * only for another render().
         */
        this._pendingRender = null;

        /*
         * Identity snapshot for which stats
         * have already been rendered successfully.
         *
         * While the snapshot has not changed, repeated
         * identity refresh does not trigger a new
         * player-stats request.
         */
        this._lastRenderedIdentityKey =
            null;
    }

    CurrentMatchStatsMonitor.prototype.restart = function (reason) {
        this._log(
            'CurrentMatchStatsMonitor: RESTART' +
            ' | reason=' + String(reason || 'screen-change')
        );

        this.stop();

        return this._startAutomatic(
            reason || 'screen-change'
        );
    };

    CurrentMatchStatsMonitor.prototype.start = function () {
        return this._startAutomatic(
            'start'
        );
    };

    CurrentMatchStatsMonitor.prototype._beginRun = function (
        runMode
    ) {
        this._cancelSandboxServiceStatus();

        this._started = true;
        this._runMode = runMode;
        this._requestRunning = false;
        this._completed = false;
        this._attemptCount = 0;
        this._pendingRender = null;
        this._lastRenderedIdentityKey = null;

        this._generation += 1;
        this._scheduleVersion += 1;

        return this._generation;
    };

    CurrentMatchStatsMonitor.prototype._startAutomatic = function (
        reason
    ) {
        if (this._started) {
            return false;
        }

        var generation =
            this._beginRun(
                'automatic'
            );

        this._log(
            'CurrentMatchStatsMonitor: START' +
            ' | mode=automatic' +
            ' | reason=' + String(reason || '') +
            ' | initialDelay=' + AUTOMATIC_START_DELAY
        );

        this._scheduleAttempt(
            AUTOMATIC_START_DELAY,
            generation,
            'automatic-start'
        );

        return true;
    };

    CurrentMatchStatsMonitor.prototype.stop = function () {
        var laneAdvisorStopped =
            false;

        if (
            this._laneAdvisorClient &&
            typeof this._laneAdvisorClient.stop ===
                'function'
        ) {
            laneAdvisorStopped =
                this._laneAdvisorClient.stop();
        }

        var hadActiveWorkflow =
            this._started ||
            this._requestRunning ||
            !!this._pendingRender ||
            !!this._currentRosterContext ||
            this._sandboxServiceStatusActive ||
            laneAdvisorStopped;

        this._clearRosterContext();

        if (
            this._statsOverlay &&
            typeof this._statsOverlay.clear ===
                'function'
        ) {
            this._statsOverlay.clear();
        }

        this._cancelSandboxServiceStatus();

        if (!hadActiveWorkflow) {
            return false;
        }

        this._started = false;
        this._runMode = null;
        this._requestRunning = false;
        this._completed = false;
        this._pendingRender = null;
        this._lastRenderedIdentityKey = null;

        this._generation += 1;
        this._scheduleVersion += 1;

        this._log(
            'CurrentMatchStatsMonitor: STOP'
        );

        return true;
    };

    CurrentMatchStatsMonitor.prototype.run = function () {
        return this.refreshNow();
    };

    CurrentMatchStatsMonitor.prototype.refreshNow = function () {
        if (this._requestRunning) {
            this._log(
                'CurrentMatchStatsMonitor: request is already in progress'
            );

            return false;
        }

        this._clearRosterContext();

        var generation =
            this._beginRun(
                'manual'
            );

        this._log(
            'CurrentMatchStatsMonitor: MANUAL REFRESH'
        );

        this._attempt(
            generation,
            'manual'
        );

        return true;
    };

    CurrentMatchStatsMonitor.prototype._scheduleAttempt = function (
        delay,
        generation,
        reason
    ) {
        var self;
        var scheduleVersion;

        if (
            !this._started ||
            this._completed ||
            generation !== this._generation
        ) {
            return;
        }

        if (
            this._attemptCount >=
            MAX_ATTEMPTS
        ) {
            if (
                this._lastRenderedIdentityKey !==
                    null
            ) {
                this._completeWorkflow(
                    generation,
                    'real-partial',
                    'max-identity-attempts'
                );
            } else {
                this._abort(
                    generation,
                    'max-attempts'
                );
            }

            return;
        }

        this._scheduleVersion += 1;

        scheduleVersion =
            this._scheduleVersion;

        self =
            this;

        this._log(
            'CurrentMatchStatsMonitor: SCHEDULE' +
            ' | delay=' + delay +
            ' | reason=' + reason
        );

        $.Schedule(
            delay,

            function () {
                if (
                    !self._started ||
                    self._completed ||
                    generation !== self._generation ||
                    scheduleVersion !== self._scheduleVersion
                ) {
                    return;
                }

                self._attempt(
                    generation,
                    'timer'
                );
            }
        );
    };

    CurrentMatchStatsMonitor.prototype._attempt = function (
        generation,
        source
    ) {
        var self;
        var callbackInvoked = false;
        var started;

        if (
            !this._isCurrent(generation) ||
            this._requestRunning
        ) {
            return;
        }

        this._requestRunning = true;
        this._attemptCount += 1;

        this._log(
            'CurrentMatchStatsMonitor: ATTEMPT' +
            ' | number=' + this._attemptCount +
            ' | source=' + source +
            ' | mode=' + this._runMode
        );

        self =
            this;

        started =
            this._matcher.matchCurrentRoster(
                function (
                    matchError,
                    matchResult
                ) {
                    callbackInvoked = true;

                    if (
                        !self._isCurrent(
                            generation
                        )
                    ) {
                        return;
                    }

                    if (matchError) {
                        self._handleMatchError(
                            generation,
                            matchError
                        );

                        return;
                    }

                    self._handleMatchResult(
                        generation,
                        matchResult
                    );
                }
            );

        if (
            !started &&
            !callbackInvoked
        ) {
            this._requestRunning = false;

            this._scheduleAttempt(
                TRANSIENT_ERROR_RETRY_DELAY,
                generation,
                'matcher-not-started'
            );
        }
    };

    CurrentMatchStatsMonitor.prototype._handleMatchError = function (
        generation,
        error
    ) {
        var delay;

        this._requestRunning = false;

        this._log(
            'CurrentMatchStatsMonitor: MATCH ERROR' +
            ' | code=' + error.code +
            ' | message=' + error.message +
            (
                error.detail !== null
                    ? ' | detail=' +
                        String(error.detail)
                    : ''
            )
        );

        if (
            error.code ===
            'roster-not-ready'
        ) {
            delay =
                this._runMode === 'manual'
                    ? MANUAL_ROSTER_RETRY_DELAY
                    : AUTOMATIC_ROSTER_RETRY_DELAY;
        } else {
            delay =
                TRANSIENT_ERROR_RETRY_DELAY;
        }

        this._scheduleAttempt(
            delay,
            generation,
            error.code || 'match-error'
        );
    };

    CurrentMatchStatsMonitor.prototype._handleMatchResult = function (
        generation,
        matchResult
    ) {
        var self =
            this;

        var callbackInvoked =
            false;

        var continued =
            false;

        var started;

        var identityKey =
            this._buildIdentityKey(
                matchResult
            );

        this._log(
            'CurrentMatchStatsMonitor: MATCHED' +
            ' | resolved=' + matchResult.resolvedCount +
            ' | ambiguous=' + matchResult.ambiguousCount +
            ' | pending=' + matchResult.pendingCount
        );

        /*
         * Continue after the Advisor client accepts the roster.
         * When Adviser is enabled this is the Bridge START ACK;
         * when it is disabled the client acknowledges locally and
         * keeps the roster for a possible later resume.
         * The lane API result itself is NOT awaited here.
         */
        function continueWorkflow() {
            if (continued) {
                return;
            }

            continued =
                true;

            if (
                !self._isCurrent(
                    generation
                )
            ) {
                return;
            }

            self._continueMatchedWorkflow(
                generation,
                matchResult,
                identityKey
            );
        }

        if (!this._laneAdvisorClient) {
            continueWorkflow();

            return;
        }

        started =
            this._laneAdvisorClient
                .startForMatches(
                    matchResult.matches,

                    function (
                        advisorError,
                        advisorResult
                    ) {
                        callbackInvoked =
                            true;

                        if (
                            !self._isCurrent(
                                generation
                            )
                        ) {
                            return;
                        }

                        if (advisorError) {
                            self._log(
                                'CurrentMatchStatsMonitor: LANE ADVISOR START ERROR' +
                                ' | code=' +
                                    advisorError.code +
                                ' | message=' +
                                    advisorError.message
                            );
                        } else {
                            self._log(
                                'CurrentMatchStatsMonitor: LANE ADVISOR ACK' +
                                ' | started=' +
                                    String(
                                        !!(
                                            advisorResult &&
                                            advisorResult.started
                                        )
                                    ) +
                                ' | deduplicated=' +
                                    String(
                                        !!(
                                            advisorResult &&
                                            advisorResult.deduplicated
                                        )
                                    )
                            );
                        }

                        continueWorkflow();
                    }
                );

        /*
         * In case of a client implementation
         * that returned false without a callback.
         */
        if (
            !started &&
            !callbackInvoked
        ) {
            this._log(
                'CurrentMatchStatsMonitor: LANE ADVISOR NOT STARTED'
            );

            continueWorkflow();
        }
    };

    /*
     * The existing stats workflow remains here.
     * We enter it after the Advisor client ACK,
     * not after the Advisor API request completes.
     */
    CurrentMatchStatsMonitor.prototype
        ._continueMatchedWorkflow =
        function (
            generation,
            matchResult,
            identityKey
        ) {
            var previewResult;

            if (
                !this._isCurrent(
                    generation
                )
            ) {
                return;
            }

            /*
             * Sandbox has also already had time
             * to send the ordered hero roster to Bridge.
             */
            if (
                this._isSandboxRoster(
                    matchResult
                )
            ) {
                this._beginSandboxServiceStatus();

                previewResult = {
                    count:
                        matchResult.matches.length,

                    okCount:
                        matchResult.matches.length,

                    statsNotFoundCount:
                        0,

                    heroUnknownCount:
                        0,

                    heroAmbiguousCount:
                        0,

                    identityUnresolvedCount:
                        0,

                    players:
                        this._buildSandboxPreviewPlayers(
                            matchResult.matches
                        )
                };

                this._finishWithStats(
                    generation,
                    previewResult,
                    'sandbox-preview',
                    identityKey,
                    matchResult
                );

                return;
            }

            if (
                matchResult.resolvedCount <
                    1
            ) {
                this._requestRunning =
                    false;

                this._log(
                    'CurrentMatchStatsMonitor: WAIT' +
                    ' | reason=no resolved players'
                );

                this._scheduleAttempt(
                    IDENTITY_REFRESH_DELAY,
                    generation,
                    'no-resolved-players'
                );

                return;
            }

            if (
                identityKey ===
                    this._lastRenderedIdentityKey
            ) {
                this._requestRunning =
                    false;

                this._log(
                    'CurrentMatchStatsMonitor: IDENTITIES UNCHANGED' +
                    ' | resolved=' +
                        matchResult.resolvedCount +
                    ' | pending=' +
                        matchResult.pendingCount +
                    ' | ambiguous=' +
                        matchResult.ambiguousCount
                );

                if (matchResult.allResolved) {
                    this._completeWorkflow(
                        generation,
                        'real',
                        'identities-already-complete'
                    );

                    return;
                }

                this._scheduleAttempt(
                    IDENTITY_REFRESH_DELAY,
                    generation,
                    'identity-refresh'
                );

                return;
            }

            this._requestRealStats(
                generation,
                matchResult,
                identityKey
            );
        };

    CurrentMatchStatsMonitor.prototype._requestRealStats = function (
        generation,
        matchResult,
        identityKey
    ) {
        var self = this;
        var callbackInvoked = false;
        var started;

        this._log(
            'CurrentMatchStatsMonitor: STATS REQUEST' +
            ' | roster=' + matchResult.matches.length +
            ' | resolved=' + matchResult.resolvedCount
        );

        started =
            this._playerStatsClient.getForMatches(
                matchResult.matches,

                function (
                    statsError,
                    statsResult
                ) {
                    callbackInvoked = true;

                    if (
                        !self._isCurrent(
                            generation
                        )
                    ) {
                        return;
                    }

                    if (statsError) {
                        self._requestRunning = false;

                        self._log(
                            'CurrentMatchStatsMonitor: STATS ERROR' +
                            ' | code=' + statsError.code +
                            ' | message=' + statsError.message +
                            (
                                statsError.detail !== null
                                    ? ' | detail=' +
                                        String(statsError.detail)
                                    : ''
                            )
                        );

                        self._scheduleAttempt(
                            TRANSIENT_ERROR_RETRY_DELAY,
                            generation,
                            'stats-error'
                        );

                        return;
                    }

                    self._finishWithStats(
                        generation,
                        statsResult,
                        'real',
                        identityKey,
                        matchResult
                    );
                }
            );

        if (
            !started &&
            !callbackInvoked
        ) {
            this._requestRunning = false;

            this._scheduleAttempt(
                TRANSIENT_ERROR_RETRY_DELAY,
                generation,
                'stats-client-not-started'
            );
        }
    };

    CurrentMatchStatsMonitor.prototype._finishWithStats = function (
        generation,
        statsResult,
        mode,
        identityKey,
        matchResult
    ) {
        if (
            !this._isCurrent(
                generation
            )
        ) {
            return;
        }

        this._requestRunning = false;

        this._logStats(
            statsResult,
            mode
        );

        this._pendingRender = {
            statsResult:
                statsResult,

            mode:
                mode,

            identityKey:
                identityKey || null,

            matchResult:
                matchResult
        };

        this._attemptPendingRender(
            generation
        );
    };

    CurrentMatchStatsMonitor.prototype._attemptPendingRender = function (
        generation
    ) {
        var pending;
        var rendered;
        var completedMode;
        var identityUnresolvedCount;
        var rosterMode;

        if (
            !this._isCurrent(generation) ||
            !this._pendingRender
        ) {
            return;
        }

        pending =
            this._pendingRender;

        rendered =
            this._statsOverlay.render(
                pending.statsResult.players
            );

        this._log(
            'CurrentMatchStatsMonitor: OVERLAY' +
            ' | rendered=' + rendered +
            ' | mode=' + pending.mode
        );

        if (!rendered) {
            this._scheduleRenderRetry(
                RENDER_RETRY_DELAY,
                generation
            );

            return;
        }

        completedMode =
            pending.mode;

        identityUnresolvedCount =
            Number(
                pending
                    .statsResult
                    .identityUnresolvedCount
            ) || 0;

        if (
            completedMode === 'real' &&
            pending.identityKey
        ) {
            this._lastRenderedIdentityKey =
                pending.identityKey;
        }

        rosterMode =
            completedMode === 'sandbox-preview'
                ? 'sandbox'
                : completedMode;

        this._publishRosterContext(
            rosterMode,
            pending.matchResult
        );

        this._pendingRender =
            null;

        this._scheduleVersion +=
            1;

        if (
            completedMode === 'real' &&
            identityUnresolvedCount > 0
        ) {
            this._log(
                'CurrentMatchStatsMonitor: PARTIAL DONE' +
                ' | attempts=' +
                    this._attemptCount +
                ' | identityUnresolved=' +
                    identityUnresolvedCount
            );

            this._scheduleAttempt(
                IDENTITY_REFRESH_DELAY,
                generation,
                'identity-refresh'
            );

            return;
        }

        this._completeWorkflow(
            generation,
            completedMode,
            'stats-complete'
        );
    };

    CurrentMatchStatsMonitor.prototype._scheduleRenderRetry = function (
        delay,
        generation
    ) {
        var self;
        var scheduleVersion;

        if (
            !this._isCurrent(generation) ||
            !this._pendingRender
        ) {
            return;
        }

        this._scheduleVersion += 1;

        scheduleVersion =
            this._scheduleVersion;

        self =
            this;

        this._log(
            'CurrentMatchStatsMonitor: RENDER SCHEDULE' +
            ' | delay=' + delay
        );

        $.Schedule(
            delay,

            function () {
                if (
                    !self._isCurrent(generation) ||
                    scheduleVersion !== self._scheduleVersion ||
                    !self._pendingRender
                ) {
                    return;
                }

                self._attemptPendingRender(
                    generation
                );
            }
        );
    };

    CurrentMatchStatsMonitor.prototype._completeWorkflow = function (
        generation,
        mode,
        reason
    ) {
        if (
            !this._started ||
            generation !==
                this._generation
        ) {
            return false;
        }

        this._requestRunning =
            false;

        this._pendingRender =
            null;

        this._completed =
            true;

        if (
            mode ===
                'sandbox-preview'
        ) {
            this._completeSandboxServiceStatus();
        }

        this._scheduleVersion +=
            1;

        this._log(
            'CurrentMatchStatsMonitor: DONE' +
            ' | attempts=' +
                this._attemptCount +
            ' | mode=' +
                String(mode || '') +
            ' | reason=' +
                String(reason || '')
        );

        return true;
    };

    CurrentMatchStatsMonitor.prototype._abort = function (
        generation,
        reason
    ) {
        var attempts;

        if (
            generation !==
            this._generation
        ) {
            return;
        }

        attempts =
            this._attemptCount;

        this._failSandboxServiceStatus();

        this._started = false;
        this._runMode = null;
        this._requestRunning = false;
        this._completed = false;
        this._pendingRender = null;
        this._lastRenderedIdentityKey = null;

        this._generation += 1;
        this._scheduleVersion += 1;
        this._clearRosterContext();

        this._log(
            'CurrentMatchStatsMonitor: ABORT' +
            ' | reason=' + reason +
            ' | attempts=' + attempts
        );
    };

    CurrentMatchStatsMonitor.prototype
        ._beginSandboxServiceStatus =
        function () {
            this._sandboxServiceStatusActive =
                true;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'winrate',
                    'in-progress'
                );
            }
        };

    CurrentMatchStatsMonitor.prototype
        ._completeSandboxServiceStatus =
        function () {
            if (!this._sandboxServiceStatusActive) {
                return;
            }

            this._sandboxServiceStatusActive =
                false;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'winrate',
                    'completed'
                );
            }
        };

    CurrentMatchStatsMonitor.prototype
        ._failSandboxServiceStatus =
        function () {
            if (!this._sandboxServiceStatusActive) {
                return;
            }

            this._sandboxServiceStatusActive =
                false;

            if (this._serviceStatusClient) {
                this._serviceStatusClient.report(
                    'winrate',
                    'error'
                );
            }
        };

    CurrentMatchStatsMonitor.prototype
        ._cancelSandboxServiceStatus =
        function () {
            this._sandboxServiceStatusActive =
                false;
        };

    CurrentMatchStatsMonitor.prototype._isCurrent = function (
        generation
    ) {
        return (
            this._started &&
            !this._completed &&
            generation === this._generation
        );
    };

    CurrentMatchStatsMonitor.prototype._isSandboxRoster = function (
        matchResult
    ) {
        if (
            !matchResult ||
            !matchResult.matches ||
            matchResult.matches.length !== 12
        ) {
            return false;
        }

        return (
            matchResult.resolvedCount === 1 &&
            matchResult.pendingCount === 11 &&
            matchResult.ambiguousCount === 0
        );
    };

    CurrentMatchStatsMonitor.prototype
        ._buildSandboxPreviewPlayers =
        function (matches) {
            var result =
                [];

            for (
                var index = 0;
                index < matches.length;
                index++
            ) {
                var match =
                    matches[index];

                var matchesPlayed =
                    12 +
                    index *
                        17;

                var desiredWinRate =
                    38 +
                    (
                        index *
                            7
                    ) %
                        27;

                var wins =
                    Math.round(
                        matchesPlayed *
                            desiredWinRate /
                            100
                    );

                var actualWinRate =
                    wins *
                        100.0 /
                        matchesPlayed;

                result.push({
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

                    panel:
                        match.panel,

                    playerNameLabel:
                        match.playerNameLabel,

                    heroNameLabel:
                        match.heroNameLabel,

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

                    statusCode:
                        0,

                    status:
                        'ok',

                    heroId:
                        0,

                    matchesPlayed:
                        matchesPlayed,

                    wins:
                        wins,

                    reaction:
                        0,

                    winRatePercent:
                        actualWinRate,

                    isSandboxPreview:
                        true
                });
            }

            return result;
        };

    CurrentMatchStatsMonitor.prototype._buildIdentityKey =
        function (matchResult) {
            if (
                !matchResult ||
                !matchResult.matches ||
                matchResult.matches.length !== 12
            ) {
                return '';
            }

            var parts =
                [];

            for (
                var index = 0;
                index < matchResult.matches.length;
                index += 1
            ) {
                var match =
                    matchResult.matches[index];

                parts.push(
                    String(match.rosterIndex) +
                    '\u001f' +
                    String(match.team || '') +
                    '\u001f' +
                    String(match.teamIndex) +
                    '\u001f' +
                    String(match.playerName || '') +
                    '\u001f' +
                    String(match.heroName || '') +
                    '\u001f' +
                    String(match.accountIdText || '') +
                    '\u001f' +
                    String(!!match.isLocal)
                );
            }

            return parts.join(
                '\u001e'
            );
        };

    CurrentMatchStatsMonitor.prototype
        ._publishRosterContext =
        function (
            mode,
            matchResult
        ) {
            if (
                !matchResult ||
                !matchResult.matches ||
                matchResult.matches.length !== 12
            ) {
                return false;
            }

            this._currentRosterContext = {
                mode:
                    mode,

                matches:
                    matchResult.matches.slice(
                        0
                    )
            };

            this._log(
                'CurrentMatchStatsMonitor: ROSTER CONTEXT READY' +
                ' | mode=' + mode +
                ' | players=' +
                    this._currentRosterContext
                        .matches.length +
                ' | afterStatsRender=true'
            );

            this._notifyRosterContextChanged(
                this._currentRosterContext
            );

            return true;
        };

    CurrentMatchStatsMonitor.prototype
        ._clearRosterContext =
        function () {
            this._currentRosterContext =
                null;

            this._notifyRosterContextChanged(
                null
            );
        };

    CurrentMatchStatsMonitor.prototype
        ._notifyRosterContextChanged =
        function (context) {
            try {
                this._onRosterContextChanged(
                    context
                );
            } catch (callbackError) {
                this._log(
                    'CurrentMatchStatsMonitor: ROSTER CONTEXT CALLBACK ERROR' +
                    ' | error=' +
                    String(callbackError)
                );
            }
        };

    CurrentMatchStatsMonitor.prototype._logStats = function (
        statsResult,
        mode
    ) {
        var index;
        var player;

        this._log(
            'Current match stats RESULT' +
            ' | mode=' + mode +
            ' | players=' + statsResult.count +
            ' | ok=' + statsResult.okCount +
            ' | statsNotFound=' +
                statsResult.statsNotFoundCount +
            ' | heroUnknown=' +
                statsResult.heroUnknownCount +
            ' | heroAmbiguous=' +
                statsResult.heroAmbiguousCount +
            ' | identityUnresolved=' +
                (
                    statsResult.identityUnresolvedCount ||
                    0
                )
        );

        for (
            index = 0;
            index < statsResult.players.length;
            index += 1
        ) {
            player =
                statsResult.players[index];

            this._log(
                'Stats [' + player.rosterIndex + ']' +
                ' | team=' + player.team +
                ' | player=' + player.playerName +
                ' | hero=' + player.heroName +
                ' | accountID=' + player.accountIdText +
                ' | status=' + player.status +
                ' | apiHeroID=' + player.heroId +
                ' | matches=' + player.matchesPlayed +
                ' | wins=' + player.wins +
                ' | winrate=' +
                    player.winRatePercent.toFixed(2) +
                    '%' +
                ' | local=' + player.isLocal
            );
        }
    };

    ThreatHud.CurrentMatchStatsMonitor =
        CurrentMatchStatsMonitor;

})(ThreatHud);
