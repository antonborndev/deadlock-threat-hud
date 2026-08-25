var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var POLL_INTERVAL =
        5.0;

    var IDENTITY_WAIT_INTERVAL =
        1.0;

    var REQUIRED_MATCH_ID_SAMPLES =
        2;

    function CurrentMatchHeroDamageMonitor(
        heroDamageClient,
        heroDamageOverlay,
        matchRosterMatcher,
        logger
    ) {
        this._client =
            heroDamageClient;

        this._overlay =
            heroDamageOverlay;

        this._matcher =
            matchRosterMatcher;

        this._log =
            typeof logger ===
                'function'
                    ? logger
                    : function () {};

        this._running =
            false;

        this._generation =
            0;

        this._context =
            null;

        /*
         * This lock survives stop/restart. The shared image transport
         * cannot cancel one request, so a new roster generation waits
         * until the previous hero-damage request has completed.
         */
        this._transportInFlight =
            false;

        this._activeRequestToken =
            0;

        this._nextRequestToken =
            0;

        this._lastRequestedAccountIds =
            null;

        this._currentSessionId =
            0;

        this._activeMatchKey =
            null;

        this._acceptedMatchKey =
            null;

        this._acceptedMatchSessionId =
            0;

        this._candidateMatchKey =
            null;

        this._candidateMatchCount =
            0;

        this._lastObservedMatchKey =
            null;

        this._retiredMatchKeys =
            [];

        this._enabled =
            false;

        this._deferredContext =
            null;
    }

    CurrentMatchHeroDamageMonitor.prototype.restart =
        function (
            context
        ) {
            this.stop();

            var contextSnapshot =
                this._createContextSnapshot(
                    context
                );

            if (!contextSnapshot) {
                this._log(
                    'CurrentMatchHeroDamageMonitor: invalid roster context'
                );

                return false;
            }

            if (!this._enabled) {
                this._deferredContext =
                    contextSnapshot;

                this._log(
                    'CurrentMatchHeroDamageMonitor: PAUSED' +
                        ' | sessionId=' +
                        contextSnapshot.sessionId
                );

                return true;
            }

            this._deferredContext =
                null;

            this._prepareMatchSession(
                contextSnapshot.sessionId
            );

            if (
                !this._overlay.bind(
                    contextSnapshot
                )
            ) {
                this._log(
                    'CurrentMatchHeroDamageMonitor: overlay bind failed' +
                        ' | sessionId=' +
                        contextSnapshot.sessionId
                );

                return false;
            }

            this._context =
                contextSnapshot;

            this._lastRequestedAccountIds =
                null;

            this._running =
                true;

            this._generation +=
                1;

            var generation =
                this._generation;

            this._log(
                'CurrentMatchHeroDamageMonitor: START' +
                    ' | sessionId=' +
                    contextSnapshot.sessionId +
                    ' | fingerprint=' +
                    contextSnapshot.fingerprint
            );

            this._attempt(
                generation
            );

            return true;
        };

    CurrentMatchHeroDamageMonitor.prototype.setEnabled =
        function (enabled) {
            var nextEnabled =
                !!enabled;

            if (
                this._enabled ===
                    nextEnabled
            ) {
                return false;
            }

            var deferredContext =
                this._context
                    ? this._createContextSnapshot(
                        this._context
                    )
                    : (
                        this._deferredContext
                            ? this._createContextSnapshot(
                                this._deferredContext
                            )
                            : null
                    );

            this._enabled =
                nextEnabled;

            if (!nextEnabled) {
                this.stop();

                this._deferredContext =
                    deferredContext;

                return true;
            }

            this._deferredContext =
                null;

            if (deferredContext) {
                this.restart(
                    deferredContext
                );
            }

            return true;
        };

    CurrentMatchHeroDamageMonitor.prototype.stop =
        function () {
            var hadState =
                this._running ||
                !!this._context ||
                !!this._deferredContext;

            this._running =
                false;

            this._generation +=
                1;

            this._context =
                null;

            this._deferredContext =
                null;

            this._lastRequestedAccountIds =
                null;

            this._candidateMatchKey =
                null;

            this._candidateMatchCount =
                0;

            this._overlay.unbind();

            if (hadState) {
                this._log(
                    'CurrentMatchHeroDamageMonitor: STOP'
                );
            }

            return hadState;
        };

    CurrentMatchHeroDamageMonitor.prototype._createContextSnapshot =
        function (
            context
        ) {
            if (
                !context ||
                typeof context.fingerprint !==
                    'string' ||
                !context.players ||
                context.players.length !==
                    EXPECTED_PLAYERS ||
                !context.panels ||
                context.panels.length !==
                    EXPECTED_PLAYERS ||
                !context.sessionId ||
                context.sessionId < 1
            ) {
                return null;
            }

            var players =
                [];

            var panels =
                [];

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var player =
                    context.players[index];

                var panel =
                    context.panels[index];

                if (
                    !player ||
                    player.rosterIndex !==
                        index ||
                    player.panel !==
                        panel ||
                    !ThreatHud.PanelUtils.isValidPanel(
                        panel
                    )
                ) {
                    return null;
                }

                players.push({
                    rosterIndex:
                        player.rosterIndex,

                    team:
                        player.team,

                    teamIndex:
                        player.teamIndex,

                    playerName:
                        player.playerName,

                    heroName:
                        player.heroName,

                    panel:
                        player.panel,

                    playerNameLabel:
                        player.playerNameLabel,

                    heroNameLabel:
                        player.heroNameLabel
                });

                panels.push(
                    panel
                );
            }

            return {
                sessionId:
                    Number(
                        context.sessionId
                    ),

                fingerprint:
                    context.fingerprint,

                players:
                    players,

                panels:
                    panels
            };
        };

    CurrentMatchHeroDamageMonitor.prototype._prepareMatchSession =
        function (
            sessionId
        ) {
            if (
                this._currentSessionId ===
                    sessionId
            ) {
                this._activeMatchKey =
                    this._acceptedMatchSessionId ===
                        sessionId
                            ? this._acceptedMatchKey
                            : null;

                this._candidateMatchKey =
                    null;

                this._candidateMatchCount =
                    0;

                return;
            }

            if (
                this._acceptedMatchKey !==
                    null
            ) {
                this._rememberRetiredMatchKey(
                    this._acceptedMatchKey
                );
            }

            if (
                this._lastObservedMatchKey !==
                    null
            ) {
                this._rememberRetiredMatchKey(
                    this._lastObservedMatchKey
                );
            }

            this._currentSessionId =
                sessionId;

            this._activeMatchKey =
                null;

            this._acceptedMatchKey =
                null;

            this._acceptedMatchSessionId =
                sessionId;

            this._candidateMatchKey =
                null;

            this._candidateMatchCount =
                0;

            this._lastObservedMatchKey =
                null;
        };

    CurrentMatchHeroDamageMonitor.prototype._rememberRetiredMatchKey =
        function (
            matchKey
        ) {
            if (
                !matchKey ||
                this._containsText(
                    this._retiredMatchKeys,
                    matchKey
                )
            ) {
                return;
            }

            this._retiredMatchKeys.push(
                matchKey
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._attempt =
        function (
            generation
        ) {
            if (
                !this._isCurrent(
                    generation
                )
            ) {
                return;
            }

            if (
                this._transportInFlight
            ) {
                this._schedule(
                    IDENTITY_WAIT_INTERVAL,
                    generation
                );

                return;
            }

            var identityMap =
                this._getValidatedIdentityMap();

            if (!identityMap) {
                this._schedule(
                    IDENTITY_WAIT_INTERVAL,
                    generation
                );

                return;
            }

            this._clearChangedIdentitySlots(
                this._lastRequestedAccountIds,
                identityMap.accountIds
            );

            this._lastRequestedAccountIds =
                identityMap.accountIds.slice(
                    0
                );

            if (
                identityMap.resolvedCount <
                    1
            ) {
                this._schedule(
                    IDENTITY_WAIT_INTERVAL,
                    generation
                );

                return;
            }

            var requestToken =
                ++this._nextRequestToken;

            var requestFingerprint =
                this._context.fingerprint;

            var requestIdentityKey =
                identityMap.key;

            var requestAccountIds =
                identityMap.accountIds.slice(
                    0
                );

            var callbackInvoked =
                false;

            this._activeRequestToken =
                requestToken;

            this._transportInFlight =
                true;

            var self =
                this;

            var started =
                this._client.getForMatches(
                    identityMap.matches,

                    function (
                        error,
                        result
                    ) {
                        callbackInvoked =
                            true;

                        self._completeRequest(
                            requestToken,
                            generation,
                            requestFingerprint,
                            requestIdentityKey,
                            requestAccountIds,
                            error,
                            result
                        );
                    }
                );

            if (
                !started &&
                !callbackInvoked
            ) {
                this._releaseRequest(
                    requestToken
                );

                this._log(
                    'CurrentMatchHeroDamageMonitor: request was not started'
                );

                this._schedule(
                    POLL_INTERVAL,
                    generation
                );
            }
        };

    CurrentMatchHeroDamageMonitor.prototype._completeRequest =
        function (
            requestToken,
            generation,
            requestFingerprint,
            requestIdentityKey,
            requestAccountIds,
            error,
            result
        ) {
            if (
                !this._releaseRequest(
                    requestToken
                )
            ) {
                return;
            }

            if (
                !this._isCurrent(
                    generation
                ) ||
                !this._context ||
                this._context.fingerprint !==
                    requestFingerprint
            ) {
                return;
            }

            var currentIdentityMap =
                this._getValidatedIdentityMap();

            if (
                !currentIdentityMap ||
                currentIdentityMap.key !==
                    requestIdentityKey
            ) {
                if (currentIdentityMap) {
                    this._clearChangedIdentitySlots(
                        requestAccountIds,
                        currentIdentityMap.accountIds
                    );

                    this._lastRequestedAccountIds =
                        currentIdentityMap.accountIds.slice(
                            0
                        );
                }

                this._log(
                    'CurrentMatchHeroDamageMonitor: stale identity response ignored'
                );

                this._schedule(
                    POLL_INTERVAL,
                    generation
                );

                return;
            }

            if (error) {
                this._log(
                    'CurrentMatchHeroDamageMonitor: REQUEST ERROR' +
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

                this._schedule(
                    POLL_INTERVAL,
                    generation
                );

                return;
            }

            this._handleResult(
                result
            );

            this._schedule(
                POLL_INTERVAL,
                generation
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._releaseRequest =
        function (
            requestToken
        ) {
            if (
                this._activeRequestToken !==
                    requestToken
            ) {
                return false;
            }

            this._activeRequestToken =
                0;

            this._transportInFlight =
                false;

            return true;
        };

    CurrentMatchHeroDamageMonitor.prototype._handleResult =
        function (
            result
        ) {
            if (!result) {
                return false;
            }

            if (
                result.state ===
                    'no-match'
            ) {
                this._activeMatchKey =
                    null;

                this._candidateMatchKey =
                    null;

                this._candidateMatchCount =
                    0;

                this._overlay.clearValues();

                return true;
            }

            var matchKey =
                result.matchKey;

            this._lastObservedMatchKey =
                matchKey;

            if (
                this._containsText(
                    this._retiredMatchKeys,
                    matchKey
                )
            ) {
                this._candidateMatchKey =
                    null;

                this._candidateMatchCount =
                    0;

                this._log(
                    'CurrentMatchHeroDamageMonitor: retired match ignored' +
                        ' | match=' +
                        matchKey +
                        ' | sessionId=' +
                        this._currentSessionId
                );

                return false;
            }

            if (
                this._activeMatchKey ===
                    matchKey ||
                (
                    this._acceptedMatchSessionId ===
                        this._currentSessionId &&
                    this._acceptedMatchKey ===
                        matchKey
                )
            ) {
                this._activeMatchKey =
                    matchKey;

                this._candidateMatchKey =
                    null;

                this._candidateMatchCount =
                    0;

                return this._applyAcceptedResult(
                    result
                );
            }

            if (
                this._candidateMatchKey ===
                    matchKey
            ) {
                this._candidateMatchCount +=
                    1;
            } else {
                this._overlay.clearValues();

                this._candidateMatchKey =
                    matchKey;

                this._candidateMatchCount =
                    1;
            }

            this._log(
                'CurrentMatchHeroDamageMonitor: MATCH CANDIDATE' +
                    ' | match=' +
                    matchKey +
                    ' | samples=' +
                    this._candidateMatchCount +
                    '/' +
                    REQUIRED_MATCH_ID_SAMPLES
            );

            if (
                this._candidateMatchCount <
                    REQUIRED_MATCH_ID_SAMPLES
            ) {
                return true;
            }

            this._activeMatchKey =
                matchKey;

            this._acceptedMatchKey =
                matchKey;

            this._acceptedMatchSessionId =
                this._currentSessionId;

            this._candidateMatchKey =
                null;

            this._candidateMatchCount =
                0;

            this._overlay.clearValues();

            this._log(
                'CurrentMatchHeroDamageMonitor: MATCH ACCEPTED' +
                    ' | match=' +
                    matchKey +
                    ' | sessionId=' +
                    this._currentSessionId
            );

            return this._applyAcceptedResult(
                result
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._applyAcceptedResult =
        function (
            result
        ) {
            if (
                result.state ===
                    'waiting'
            ) {
                return true;
            }

            if (
                result.state !==
                    'snapshot'
            ) {
                return false;
            }

            if (
                this._overlay.apply(
                    result.players
                )
            ) {
                return true;
            }

            /*
             * A native KDA subtree may be rebuilt while the cached
             * player panel remains valid. Repair only after the cached
             * binding actually failed; the normal polling path never
             * traverses the HUD tree.
             */
            this._log(
                'CurrentMatchHeroDamageMonitor: repairing overlay binding'
            );

            if (
                !this._overlay.bind(
                    this._context
                )
            ) {
                return false;
            }

            return this._overlay.apply(
                result.players
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._getValidatedIdentityMap =
        function () {
            if (
                !this._context ||
                !this._matcher ||
                typeof this._matcher.getLastResult !==
                    'function'
            ) {
                return null;
            }

            var result =
                this._matcher.getLastResult();

            if (
                !result ||
                !result.matches ||
                result.matches.length !==
                    EXPECTED_PLAYERS
            ) {
                return null;
            }

            var matches =
                [];

            var accountIds =
                [];

            var nonZeroAccountIds =
                [];

            var resolvedCount =
                0;

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var match =
                    result.matches[index];

                var player =
                    this._context.players[index];

                var panel =
                    this._context.panels[index];

                if (
                    !match ||
                    !player ||
                    match.rosterIndex !==
                        index ||
                    player.rosterIndex !==
                        index ||
                    match.team !==
                        player.team ||
                    match.teamIndex !==
                        player.teamIndex ||
                    match.playerName !==
                        player.playerName ||
                    match.heroName !==
                        player.heroName ||
                    match.panel !==
                        panel ||
                    player.panel !==
                        panel ||
                    match.playerNameLabel !==
                        player.playerNameLabel ||
                    match.heroNameLabel !==
                        player.heroNameLabel
                ) {
                    return null;
                }

                var accountIdText =
                    '0';

                if (
                    match.status ===
                        'resolved'
                ) {
                    accountIdText =
                        String(
                            match.accountIdText ||
                                match.accountId ||
                                ''
                        );

                    if (
                        !/^\d+$/.test(
                            accountIdText
                        ) ||
                        accountIdText ===
                            '0' ||
                        this._containsText(
                            nonZeroAccountIds,
                            accountIdText
                        )
                    ) {
                        return null;
                    }

                    var numericAccountId =
                        Number(
                            accountIdText
                        );

                    if (
                        !isFinite(
                            numericAccountId
                        ) ||
                        numericAccountId < 1 ||
                        numericAccountId >
                            4294967295 ||
                        Math.floor(
                            numericAccountId
                        ) !==
                            numericAccountId
                    ) {
                        return null;
                    }

                    nonZeroAccountIds.push(
                        accountIdText
                    );

                    resolvedCount +=
                        1;
                }

                accountIds.push(
                    accountIdText
                );

                matches.push({
                    status:
                        match.status,

                    rosterIndex:
                        match.rosterIndex,

                    team:
                        match.team,

                    teamIndex:
                        match.teamIndex,

                    playerName:
                        match.playerName,

                    heroName:
                        match.heroName,

                    panel:
                        match.panel,

                    playerNameLabel:
                        match.playerNameLabel,

                    heroNameLabel:
                        match.heroNameLabel,

                    accountId:
                        accountIdText ===
                            '0'
                                ? 0
                                : Number(
                                    accountIdText
                                ),

                    accountIdText:
                        accountIdText
                });
            }

            return {
                matches:
                    matches,

                accountIds:
                    accountIds,

                resolvedCount:
                    resolvedCount,

                key:
                    accountIds.join(
                        '|'
                    )
            };
        };

    CurrentMatchHeroDamageMonitor.prototype._clearChangedIdentitySlots =
        function (
            previous,
            current
        ) {
            if (
                !previous ||
                !current ||
                previous.length !==
                    EXPECTED_PLAYERS ||
                current.length !==
                    EXPECTED_PLAYERS
            ) {
                return 0;
            }

            var changedIndexes =
                [];

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                if (
                    previous[index] !==
                        current[index]
                ) {
                    changedIndexes.push(
                        index
                    );
                }
            }

            if (
                changedIndexes.length ===
                    0
            ) {
                return 0;
            }

            this._log(
                'CurrentMatchHeroDamageMonitor: IDENTITY MAP CHANGED' +
                    ' | slots=' +
                    changedIndexes.join(
                        ','
                    )
            );

            return this._overlay.clearSlots(
                changedIndexes
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._schedule =
        function (
            delay,
            generation
        ) {
            var self =
                this;

            $.Schedule(
                delay,

                function () {
                    if (
                        !self._isCurrent(
                            generation
                        )
                    ) {
                        return;
                    }

                    self._attempt(
                        generation
                    );
                }
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._isCurrent =
        function (
            generation
        ) {
            return !!(
                this._running &&
                this._context &&
                generation ===
                    this._generation
            );
        };

    CurrentMatchHeroDamageMonitor.prototype._containsText =
        function (
            values,
            expected
        ) {
            for (
                var index = 0;
                index < values.length;
                index += 1
            ) {
                if (
                    values[index] ===
                        expected
                ) {
                    return true;
                }
            }

            return false;
        };

    ThreatHud.CurrentMatchHeroDamageMonitor =
        CurrentMatchHeroDamageMonitor;

})(ThreatHud);
