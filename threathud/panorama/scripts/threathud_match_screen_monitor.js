var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS = 12;
    var PLAYERS_PER_TEAM = 6;

    /*
     * A new presence snapshot must
     * match twice in a row.
     */
    var REQUIRED_STABLE_SAMPLES = 2;

    var INITIAL_DISCOVERY_DELAY = 0.5;
    var DISCOVERY_INTERVAL = 1.0;

    /*
     * Matcher builds the roster cache.
     * Screen monitor only waits for it to appear.
     *
     * 200 × 0.1 seconds = 20 seconds maximum.
     */
    var CACHE_WAIT_INTERVAL = 0.10;
    var MAX_CACHE_WAIT_SAMPLES = 200;

    /*
     * One cached slot every 0.1 seconds.
     *
     * A full cycle over 12 players takes
     * approximately 1.2 seconds.
     */
    var WATCH_INTERVAL = 1.0;

    var REDISCOVERY_DELAY = 0.25;

    /*
    * For the first two minutes after
    * the first full roster appears,
    * an additional player-order check is performed.
    */
    var EARLY_RECHECK_INTERVAL =
        1.0;

    var EARLY_RECHECK_WINDOW_MS =
        90000;
        

    function MatchScreenMonitor(
        matchRoster,
        currentMatchStatsMonitor,
        currentMatchHeroDamageMonitor,
        logger,
        onStableWindowReady
    ) {
        this._matchRoster = matchRoster;
        this._statsMonitor = currentMatchStatsMonitor;
        this._heroDamageMonitor =
            currentMatchHeroDamageMonitor &&
            typeof currentMatchHeroDamageMonitor.restart ===
                'function' &&
            typeof currentMatchHeroDamageMonitor.stop ===
                'function'
                    ? currentMatchHeroDamageMonitor
                    : {
                        restart:
                            function () {
                                return false;
                            },

                        stop:
                            function () {
                                return false;
                            }
                    };
        this._log =
            typeof logger === 'function'
                ? logger
                : function () {};

        /*
        * Called once after:
        *
        * - all 12 players have already been found;
        * - the two-minute early recheck window has ended;
        * - the roster is considered stable.
        *
        * The next step attached to this callback
        * will start the rank logic.
        */
        this._onStableWindowReady =
            typeof onStableWindowReady === 'function'
                ? onStableWindowReady
                : function () {};

        /*
        * Prevent the callback from firing again
        * within the same game session.
        */
        this._stableWindowReadyFired =
            false;

        this._running = false;
        this._generation = 0;

        /*
         * discover:
         *     search only for 6 + 6 player panels.
         *
         * await-cache:
         *     matcher is running; wait for the roster cache.
         *
         * watch:
         *     check cached slots.
         */
        this._mode = 'discover';

        this._watchIndex = 0;
        this._cacheWaitCount = 0;

        this._candidateSnapshot = null;
        this._candidateCount = 0;

        /*
         * Contains 12 panel references,
         * but does not yet contain names and heroes.
         */
        this._pendingPresenceSnapshot = null;

        /*
         * Full roster snapshot after
         * the matcher called readPlayers().
         */
        this._stableSnapshot = null;

        this._screenState = 'initial';

        /*
         * Monotonic Panorama match-session generation.
         * It changes only after initial/no-roster -> roster,
         * never during rediscovery inside the same match.
         */
        this._matchSessionId =
            0;

        /*
        * Absolute end time of
        * the two-minute window.
        *
        * It is not reset when players are rearranged
        * players within the same game session.
        */
        this._earlyRecheckDeadlineMs =
            0;

        /*
        * Version of the separate 10-second timer.
        *
        * Allows old callbacks to be canceled
        * during rediscovery or when leaving the match.
        */
        this._earlyRecheckVersion =
            0;

    }

    MatchScreenMonitor.prototype.start = function () {
        if (this._running) {
            return false;
        }

        this._running = true;
        this._generation += 1;

        this._mode = 'discover';
        this._watchIndex = 0;
        this._cacheWaitCount = 0;

        this._candidateSnapshot = null;
        this._candidateCount = 0;

        this._pendingPresenceSnapshot = null;
        this._stableSnapshot = null;
        this._screenState = 'initial';
        this._stableWindowReadyFired = false;

        this._earlyRecheckDeadlineMs = 0;

        this._earlyRecheckVersion++;

        this._log(
            'MatchScreenMonitor: START'
        );

        this._schedule(
            INITIAL_DISCOVERY_DELAY,
            this._generation
        );

        return true;
    };

    MatchScreenMonitor.prototype.stop = function () {
        if (!this._running) {
            return false;
        }

        this._running = false;
        this._generation += 1;

        this._mode = 'discover';
        this._watchIndex = 0;
        this._cacheWaitCount = 0;

        this._candidateSnapshot = null;
        this._candidateCount = 0;

        this._pendingPresenceSnapshot = null;
        this._stableSnapshot = null;
        this._screenState = 'initial';
        this._stableWindowReadyFired = false;

        this._earlyRecheckDeadlineMs = 0;

        this._earlyRecheckVersion++;

        this._heroDamageMonitor.stop();
        this._statsMonitor.stop();
        this._matchRoster.invalidate();

        this._log(
            'MatchScreenMonitor: STOP'
        );

        return true;
    };

    MatchScreenMonitor.prototype._schedule = function (
        delay,
        generation
    ) {
        var self =
            this;

        if (
            !this._running ||
            generation !== this._generation
        ) {
            return;
        }

        $.Schedule(
            delay,

            function () {
                if (
                    !self._running ||
                    generation !== self._generation
                ) {
                    return;
                }

                if (
                    self._mode ===
                    'watch'
                ) {
                    self._pollCachedSlot(
                        generation
                    );

                    return;
                }

                if (
                    self._mode ===
                    'await-cache'
                ) {
                    self._pollRosterCache(
                        generation
                    );

                    return;
                }

                self._pollDiscovery(
                    generation
                );
            }
        );
    };

    /*
     * ============================================================
     * DISCOVERY
     * ============================================================
     *
     * Checks only for the presence of:
     *
     * 6 ally panels
     * 6 enemy panels
     *
     * readPlayers() is no longer called here.
     */
    MatchScreenMonitor.prototype._pollDiscovery = function (
        generation
    ) {
        var snapshot =
            this._readPresenceSnapshot();

        if (
            this._samePresenceSnapshot(
                snapshot,
                this._candidateSnapshot
            )
        ) {
            this._candidateCount += 1;
        } else {
            this._candidateSnapshot =
                snapshot;

            this._candidateCount =
                1;
        }

        if (
            this._candidateCount >=
            REQUIRED_STABLE_SAMPLES
        ) {
            this._acceptPresenceSnapshot(
                snapshot
            );
        }

        this._schedule(
            this._nextInterval(),
            generation
        );
    };

    MatchScreenMonitor.prototype._readPresenceSnapshot = function () {
        var allyPanels;
        var enemyPanels;

        /*
         * connect() must use the stored
         * team/container references while they remain valid.
         */
        if (
            !this._matchRoster.connect()
        ) {
            return this._createEmptyPresenceSnapshot();
        }

        allyPanels =
            this._matchRoster.getPlayerPanels(
                'ally'
            );

        enemyPanels =
            this._matchRoster.getPlayerPanels(
                'enemy'
            );

        if (
            allyPanels.length !== PLAYERS_PER_TEAM ||
            enemyPanels.length !== PLAYERS_PER_TEAM
        ) {
            return this._createEmptyPresenceSnapshot();
        }

        return {
            state:
                'roster',

            panels:
                allyPanels.concat(
                    enemyPanels
                )
        };
    };

    MatchScreenMonitor.prototype._createEmptyPresenceSnapshot =
        function () {
            return {
                state:
                    'no-roster',

                panels:
                    []
            };
        };

    MatchScreenMonitor.prototype._samePresenceSnapshot = function (
        left,
        right
    ) {
        var index;

        if (
            !left ||
            !right ||
            left.state !== right.state
        ) {
            return false;
        }

        if (
            left.state ===
            'no-roster'
        ) {
            return true;
        }

        if (
            left.panels.length !==
            right.panels.length
        ) {
            return false;
        }

        for (
            index = 0;
            index < left.panels.length;
            index += 1
        ) {
            if (
                left.panels[index] !==
                right.panels[index]
            ) {
                return false;
            }
        }

        return true;
    };

    MatchScreenMonitor.prototype._acceptPresenceSnapshot = function (
        snapshot
    ) {
        if (
            snapshot.state ===
            'no-roster'
        ) {
            if (
                this._screenState ===
                'no-roster'
            ) {
                return;
            }

            this._log(
                'MatchScreenMonitor: SCREEN CHANGED' +
                ' | from=' + this._screenState +
                ' | to=no-roster'
            );

            this._screenState = 'no-roster';
            this._pendingPresenceSnapshot = null;
            this._stableSnapshot = null;
            this._watchIndex = 0;
            this._cacheWaitCount = 0;

            /*
            * The full roster disappeared.
            *
            * The next appearance of 12 players will be
            * treated as a new game session
            * with a new two-minute window.
            */
            this._earlyRecheckDeadlineMs = 0;

            this._earlyRecheckVersion++;

            /*
            * Complete exit from the match / sandbox.
            * The next roster appearance is
            * a new game session.
            */
            this._stableWindowReadyFired =
                false;


            /*
             * stop() is idempotent.
             *
             * If the workflow has already been stopped
             * during invalidation, the call does nothing.
             */
            this._heroDamageMonitor.stop();
            this._statsMonitor.stop();

            return;
        }

        this._log(
            'MatchScreenMonitor: SCREEN CHANGED' +
            ' | from=' + this._screenState +
            ' | to=roster'
        );

        /*
        * The window starts only on the first
        * roster appearance after initial/no-roster.
        *
        * When players are rearranged within the same
        * session, the deadline is not extended by
        * another two minutes.
        */
        if (
            this._screenState ===
                'initial' ||
            this._screenState ===
                'no-roster'
        ) {
            this._matchSessionId +=
                1;

            this._earlyRecheckDeadlineMs =
                new Date()
                    .getTime() +
                EARLY_RECHECK_WINDOW_MS;

            this._stableWindowReadyFired = false;

            this._earlyRecheckVersion++;

            this._log(
                'MatchScreenMonitor: EARLY RECHECK START' +
                    ' | durationSeconds=90' +
                    ' | intervalSeconds=10'
            );
        }

        this._screenState =
            'roster-loading';

        this._pendingPresenceSnapshot =
            snapshot;

        this._cacheWaitCount =
            0;

        this._mode =
            'await-cache';

        /*
         * Now only the matcher will call
         * MatchRoster.readPlayers().
         */
        this._statsMonitor.restart(
            'match-screen-roster'
        );
    };

    /*
     * ============================================================
     * AWAIT CACHE
     * ============================================================
     *
     * MatchRosterMatcher is the only component that calls
     * readPlayers().
     *
     * Here we only retrieve already prepared:
     *
     * getPlayers()
     * getCachedPanelReferences()
     * getFingerprint()
     */
    MatchScreenMonitor.prototype._pollRosterCache = function (
        generation
    ) {
        var players;
        var panels;

        if (
            !this._pendingPresenceSnapshot ||
            !this._arePanelsValid(
                this._pendingPresenceSnapshot.panels
            )
        ) {
            this._beginRediscovery(
                generation,
                'presence-panels-invalid'
            );

            return;
        }

        players =
            this._matchRoster.getPlayers();

        panels =
            this._matchRoster.getCachedPanelReferences();

        if (
            players.length === EXPECTED_PLAYERS &&
            panels.length === EXPECTED_PLAYERS &&
            this._samePanelReferences(
                panels,
                this._pendingPresenceSnapshot.panels
            )
        ) {
            this._stableSnapshot = {
                state:
                    'roster',

                sessionId:
                    this._matchSessionId,

                fingerprint:
                    this._matchRoster.getFingerprint(),

                players:
                    players,

                panels:
                    panels
            };

            this._pendingPresenceSnapshot = null;
            this._cacheWaitCount = 0;
            this._watchIndex = 0;
            this._mode = 'watch';
            this._screenState = 'roster';

            this._log(
                'MatchScreenMonitor: ROSTER CACHE READY' +
                ' | players=' + players.length +
                ' | sessionId=' +
                    this._matchSessionId
            );

            /*
             * Hero damage owns no HUD discovery. It receives this exact
             * cached roster generation and binds its KDA children once.
             */
            this._heroDamageMonitor.restart(
                this._stableSnapshot
            );

            /*
            * A separate ten-second
            * audit of the current panel order is started.
            */
            this._scheduleEarlyRecheck(
                generation
            );


            this._schedule(
                WATCH_INTERVAL,
                generation
            );

            return;
        }

        this._cacheWaitCount += 1;

        if (
            this._cacheWaitCount >=
            MAX_CACHE_WAIT_SAMPLES
        ) {
            this._beginRediscovery(
                generation,
                'roster-cache-timeout'
            );

            return;
        }

        this._schedule(
            CACHE_WAIT_INTERVAL,
            generation
        );
    };

    /*
     * ============================================================
     * WATCH
     * ============================================================
     *
     * Checks one cached slot.
     *
     * There is no tree traversal.
     */
    MatchScreenMonitor.prototype._pollCachedSlot = function (
        generation
    ) {
        var expectedPlayer;
        var currentPlayer;

        if (
            !this._stableSnapshot ||
            this._stableSnapshot.state !== 'roster'
        ) {
            this._beginRediscovery(
                generation,
                'stable-roster-missing'
            );

            return;
        }

        expectedPlayer =
            this._stableSnapshot.players[
                this._watchIndex
            ];

        currentPlayer =
            this._matchRoster.readCachedPlayer(
                this._watchIndex
            );

        if (
            !currentPlayer ||
            !expectedPlayer ||
            currentPlayer.panel !==
                this._stableSnapshot.panels[
                    this._watchIndex
                ] ||
            currentPlayer.playerName !==
                expectedPlayer.playerName ||
            currentPlayer.heroName !==
                expectedPlayer.heroName
        ) {
            this._beginRediscovery(
                generation,
                'cached-slot-changed-' +
                    this._watchIndex
            );

            return;
        }

        this._watchIndex += 1;

        if (
            this._watchIndex >=
            EXPECTED_PLAYERS
        ) {
            this._watchIndex = 0;
        }

        this._schedule(
            WATCH_INTERVAL,
            generation
        );
    };

    MatchScreenMonitor.prototype._beginRediscovery = function (
        generation,
        reason
    ) {
        this._log(
            'MatchScreenMonitor: CACHE INVALIDATED' +
            ' | reason=' + reason
        );

        /*
        * Cancel the already scheduled early audit.
        *
        * The deadline itself is not cleared:
        * player rearrangement does not start
        * a new two-minute period.
        */
        this._earlyRecheckVersion++;
        /*
        * The current stable roster is no longer valid.
        *
        * After rediscovery completes, the new roster
        * must send STABLE WINDOW READY again.
        *
        * The two-minute deadline does not start
        * again: if the window has already ended, the new
        * signal will be sent immediately once
        * the new roster cache is ready.
        */
        this._stableWindowReadyFired =
            false;


        this._heroDamageMonitor.stop();
        this._statsMonitor.stop();
        this._matchRoster.invalidate();

        this._mode = 'discover';
        this._watchIndex = 0;
        this._cacheWaitCount = 0;

        this._candidateSnapshot = null;
        this._candidateCount = 0;

        this._pendingPresenceSnapshot = null;
        this._stableSnapshot = null;

        this._schedule(
            REDISCOVERY_DELAY,
            generation
        );
    };

    MatchScreenMonitor.prototype._nextInterval = function () {
        if (
            this._mode ===
            'watch'
        ) {
            return WATCH_INTERVAL;
        }

        if (
            this._mode ===
            'await-cache'
        ) {
            return CACHE_WAIT_INTERVAL;
        }

        return DISCOVERY_INTERVAL;
    };

    MatchScreenMonitor.prototype._arePanelsValid = function (
        panels
    ) {
        var index;

        if (
            !panels ||
            panels.length !== EXPECTED_PLAYERS
        ) {
            return false;
        }

        for (
            index = 0;
            index < panels.length;
            index += 1
        ) {
            if (
                !ThreatHud.PanelUtils.isValidPanel(
                    panels[index]
                )
            ) {
                return false;
            }
        }

        return true;
    };

    MatchScreenMonitor.prototype._samePanelReferences = function (
        left,
        right
    ) {
        var index;

        if (
            !left ||
            !right ||
            left.length !== right.length
        ) {
            return false;
        }

        for (
            index = 0;
            index < left.length;
            index += 1
        ) {
            if (
                left[index] !==
                right[index]
            ) {
                return false;
            }
        }

        return true;
    };

    /*
    * Schedules the next check of the current
    * player order in 10 seconds.
    */
    MatchScreenMonitor.prototype
    ._scheduleEarlyRecheck =
    function (generation) {
        if (
            !this._running ||
            generation !==
                this._generation ||
            this._mode !==
                'watch' ||
            !this._stableSnapshot
        ) {
            return;
        }

        var now =
            new Date()
                .getTime();

        var remainingMs =
            this._earlyRecheckDeadlineMs -
            now;

        /*
         * The two-minute window has actually
         * ended.
         *
         * The signal is not emitted early.
         */
        if (
            this._earlyRecheckDeadlineMs <= 0 ||
            remainingMs <= 0
        ) {
            this._earlyRecheckDeadlineMs =
                0;

            this._earlyRecheckVersion++;

            this._log(
                'MatchScreenMonitor: EARLY RECHECK DONE'
            );

            this._notifyStableWindowReady();

            return;
        }

        /*
         * Normally the next check is performed
         * in 10 seconds.
         *
         * If less time remains before the window ends,
         * schedule the check for exactly the remaining
         * time instead of ending the window early.
         */
        var delay =
            Math.min(
                EARLY_RECHECK_INTERVAL,
                remainingMs / 1000.0
            );

        this._earlyRecheckVersion++;

        var earlyVersion =
            this._earlyRecheckVersion;

        var self =
            this;

        $.Schedule(
            delay,

            function () {
                if (
                    !self._running ||
                    generation !==
                        self._generation ||
                    earlyVersion !==
                        self._earlyRecheckVersion ||
                    self._mode !==
                        'watch'
                ) {
                    return;
                }

                self._runEarlyRecheck(
                    generation
                );
            }
        );
    };

    /*
    * Performs one full check of the current
    * order of all 12 players.
    *
    * It does not search the HUD again; it uses:
    *
    * - already stored PlayersContainer references;
    * - the current Children() order;
    * - already stored Label references.
    */
    MatchScreenMonitor.prototype
	._runEarlyRecheck =
	function (generation) {
		var liveSnapshot =
			this._matchRoster
				.readLiveOrderedSnapshot();

		if (!liveSnapshot) {
			this._beginRediscovery(
				generation,
				'early-recheck-invalid'
			);

			return;
		}

		/*
		 * Check simultaneously:
		 *
		 * - the current order of panel references;
		 * - playerName;
		 * - heroName;
		 * - teamIndex;
		 * - rosterIndex.
		 */
		if (
			liveSnapshot.fingerprint !==
				this._stableSnapshot
					.fingerprint ||
			!this._samePanelReferences(
				liveSnapshot.panels,
				this._stableSnapshot
					.panels
			)
		) {
			this._log(
				'MatchScreenMonitor: EARLY ROSTER CHANGED'
			);

			this._beginRediscovery(
				generation,
				'early-roster-changed'
			);

			return;
		}

		/*
		 * The roster has not changed.
		 * Schedule the next check.
		 */
		this._scheduleEarlyRecheck(
			generation
		);
	};

    /*
    * Notifies external code once that:
    *
    * - the early two-minute stabilization has completed;
    * - secondary heavy logic may now be started,
    *   for example, fetching ranks.
    */
    MatchScreenMonitor.prototype
	._notifyStableWindowReady =
	function () {
		if (
			this._stableWindowReadyFired
		) {
			return;
		}

		if (
			!this._stableSnapshot ||
			this._stableSnapshot.state !==
				'roster'
		) {
			return;
		}

		this._stableWindowReadyFired =
			true;

		this._log(
			'MatchScreenMonitor: STABLE WINDOW READY'
		);

		this._onStableWindowReady({
			fingerprint:
				this._stableSnapshot
					.fingerprint,

			players:
				this._stableSnapshot
					.players,

			panels:
				this._stableSnapshot
					.panels
		});
	};

    ThreatHud.MatchScreenMonitor =
        MatchScreenMonitor;

})(ThreatHud);
