var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var ReactionValue =
        ThreatHud.PlayerReactionValue;

    function CurrentMatchReactionMonitor(
        reactionClient,
        reactionOverlay,
        statsOverlay,
        logger
    ) {
        this._reactionClient =
            reactionClient;

        this._reactionOverlay =
            reactionOverlay;

        this._statsOverlay =
            statsOverlay;

        this._log =
            typeof logger === 'function'
                ? logger
                : function () {};

        /*
         * The last successfully rendered stats result.
         *
         * This contains:
         * accountId + reaction + native panel.
         */
        this._statsContext =
            null;

        /*
         * The last rank result for which
         * CurrentMatchRankOverlay.render()
         * has already returned true.
         */
        this._ranksReadyContext =
            null;

        /*
         * Any change of input context invalidates
         * an unfinished reaction write callback.
         */
        this._generation =
            0;

        var self =
            this;

        this._reactionRequestedHandler =
            function (
                player,
                requestedReaction
            ) {
                self._requestReaction(
                    player,
                    requestedReaction
                );
            };
    }

    CurrentMatchReactionMonitor.prototype
        ._buildSandboxReactionPlayers =
        function (players) {
            var result =
                [];

            var botIndex =
                0;

            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                var source =
                    players[index];

                var player =
                    {};

                for (var key in source) {
                    if (
                        source.hasOwnProperty(
                            key
                        )
                    ) {
                        player[key] =
                            source[key];
                    }
                }

                /*
                 * For the test local slot, reaction
                 * controls remain disabled.
                 */
                if (source.isLocal) {
                    player.accountId =
                        0;

                    player.accountIdText =
                        '';

                    player.reaction =
                        ReactionValue.none;
                } else {
                    /*
                     * The 11 bots receive a persistent
                     * synthetic player-only accountID:
                     *
                     * botIndex 0..10
                     * accountID 1..11
                     */
                    player.sandboxBotIndex =
                        botIndex;

                    player.accountId =
                        botIndex + 1;

                    player.accountIdText =
                        String(
                            botIndex + 1
                        );

                    player.reaction =
                        ReactionValue.none;

                    botIndex += 1;
                }

                result.push(
                    player
                );
            }

            return result;
        };

    CurrentMatchReactionMonitor.prototype.acceptStatsPlayers =
        function (players) {
            this._generation += 1;

            this._clearOverlay(
                'stats-context-changed'
            );

            if (!players) {
                this._statsContext =
                    null;

                return true;
            }

            if (
                !this._isValidPlayers(
                    players
                )
            ) {
                this._statsContext =
                    null;

                this._log(
                    'CurrentMatchReactionMonitor: STATS REJECTED' +
                        ' | reason=invalid-players'
                );

                return false;
            }

            var mode =
                this._detectMode(
                    players
                );

            this._statsContext = {
                mode:
                    mode,

                players:
                    mode === 'sandbox'
                        ? this._buildSandboxReactionPlayers(
                            players
                        )
                        : players.slice(
                            0
                        )
            };

            this._log(
                'CurrentMatchReactionMonitor: STATS READY' +
                    ' | mode=' +
                    this._statsContext.mode +
                    ' | players=' +
                    players.length
            );

            this._tryRender();

            return true;
        };

    CurrentMatchReactionMonitor.prototype.acceptRanksReadyPlayers =
        function (players) {
            this._generation += 1;

            this._clearOverlay(
                'ranks-ready-context-changed'
            );

            if (!players) {
                this._ranksReadyContext =
                    null;

                return true;
            }

            if (
                !this._isValidPlayers(
                    players
                )
            ) {
                this._ranksReadyContext =
                    null;

                this._log(
                    'CurrentMatchReactionMonitor: RANKS REJECTED' +
                        ' | reason=invalid-players'
                );

                return false;
            }

            this._ranksReadyContext = {
                mode:
                    this._detectMode(
                        players
                    ),

                players:
                    players.slice(
                        0
                    )
            };

            this._log(
                'CurrentMatchReactionMonitor: RANKS READY' +
                    ' | mode=' +
                    this._ranksReadyContext.mode +
                    ' | players=' +
                    players.length
            );

            this._tryRender();

            return true;
        };

    CurrentMatchReactionMonitor.prototype._tryRender =
        function () {
            if (
                !this._statsContext ||
                !this._ranksReadyContext
            ) {
                return false;
            }

            if (
                this._statsContext.mode !==
                    this._ranksReadyContext.mode
            ) {
                this._log(
                    'CurrentMatchReactionMonitor: WAIT' +
                        ' | reason=mode-mismatch'
                );

                return false;
            }

            if (
                !this._samePlayers(
                    this._statsContext.players,
                    this._ranksReadyContext.players
                )
            ) {
                this._log(
                    'CurrentMatchReactionMonitor: WAIT' +
                        ' | reason=context-mismatch'
                );

                return false;
            }

            var rendered =
                false;

            try {
                rendered =
                    this._reactionOverlay.render(
                        this._statsContext.players,
                        this._reactionRequestedHandler
                    );
            } catch (renderError) {
                this._log(
                    'CurrentMatchReactionMonitor: OVERLAY ERROR' +
                        ' | error=' +
                        String(
                            renderError
                        )
                );

                return false;
            }

            this._log(
                'CurrentMatchReactionMonitor: OVERLAY' +
                    ' | rendered=' +
                    rendered
            );

            return rendered;
        };

    /*
     * The only place where reaction state is changed.
     *
     * Both representations are updated as one group:
     *
     * - active button state;
     * - symbol in the winrate badge.
     *
     * player.reaction is changed only after
     * both overlays have accepted the new value.
     */
    CurrentMatchReactionMonitor.prototype._setReactionState =
        function (
            player,
            reaction
        ) {
            if (
                !player ||
                !ReactionValue.isValid(
                    reaction
                )
            ) {
                return false;
            }

            var previousReaction =
                player.reaction;

            var overlays = [
                this._reactionOverlay,
                this._statsOverlay
            ];

            var updatedOverlays =
                [];

            for (
                var index = 0;
                index < overlays.length;
                index += 1
            ) {
                var overlay =
                    overlays[index];

                var updated =
                    false;

                try {
                    updated = !!(
                        overlay &&
                        typeof overlay.updateReaction ===
                            'function' &&
                        overlay.updateReaction(
                            player,
                            reaction
                        )
                    );
                } catch (updateError) {
                    updated =
                        false;
                }

                if (updated) {
                    updatedOverlays.push(
                        overlay
                    );

                    continue;
                }

                /*
                 * Do not leave the two overlays
                 * in different states.
                 */
                for (
                    var rollbackIndex =
                        updatedOverlays.length - 1;
                    rollbackIndex >= 0;
                    rollbackIndex -= 1
                ) {
                    try {
                        updatedOverlays[
                            rollbackIndex
                        ].updateReaction(
                            player,
                            previousReaction
                        );
                    } catch (rollbackError) {
                    }
                }

                return false;
            }

            player.reaction =
                reaction;

            return true;
        };

    CurrentMatchReactionMonitor.prototype._rollbackReactionWrite =
        function (
            player,
            previousReaction,
            detail
        ) {
            var restored =
                this._setReactionState(
                    player,
                    previousReaction
                );

            this._setPending(
                player,
                false
            );

            this._log(
                'CurrentMatchReactionMonitor: WRITE ERROR' +
                    ' | ' +
                    String(
                        detail ||
                        'reason=unknown-error'
                    ) +
                    ' | rollback=' +
                    previousReaction +
                    ' | restored=' +
                    restored
            );

            return false;
        };

    CurrentMatchReactionMonitor.prototype._isValidWriteAck =
        function (
            result,
            player
        ) {
            return !!(
                result &&
                player &&
                result.accountId ===
                    player.accountId &&
                ReactionValue.isValid(
                    result.reaction
                )
            );
        };

    CurrentMatchReactionMonitor.prototype._handleReactionWriteResult =
        function (
            generation,
            expectedPlayer,
            previousReaction,
            error,
            result
        ) {
            if (
                generation !==
                    this._generation
            ) {
                return false;
            }

            var latestPlayer =
                this._findCurrentStatsPlayer(
                    expectedPlayer
                );

            if (!latestPlayer) {
                return false;
            }

            if (error) {
                return this._rollbackReactionWrite(
                    latestPlayer,
                    previousReaction,
                    'code=' +
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
            }

            if (
                !this._isValidWriteAck(
                    result,
                    latestPlayer
                )
            ) {
                return this._rollbackReactionWrite(
                    latestPlayer,
                    previousReaction,
                    'reason=ack-mismatch'
                );
            }

            if (
                latestPlayer.reaction !==
                    result.reaction &&
                !this._setReactionState(
                    latestPlayer,
                    result.reaction
                )
            ) {
                return this._rollbackReactionWrite(
                    latestPlayer,
                    previousReaction,
                    'reason=ack-state-update-failed'
                );
            }

            this._setPending(
                latestPlayer,
                false
            );

            this._log(
                'CurrentMatchReactionMonitor: WRITE DONE' +
                    ' | accountId=' +
                    result.accountId +
                    ' | reaction=' +
                    result.reaction
            );

            return true;
        };

    CurrentMatchReactionMonitor.prototype._requestReaction =
        function (
            player,
            requestedReaction
        ) {
            if (
                requestedReaction !==
                    ReactionValue.dislike &&
                requestedReaction !==
                    ReactionValue.like
            ) {
                return false;
            }

            var currentPlayer =
                this._findCurrentStatsPlayer(
                    player
                );

            if (!currentPlayer) {
                this._log(
                    'CurrentMatchReactionMonitor: WRITE REJECTED' +
                        ' | reason=stale-player'
                );

                return false;
            }

            if (
                !ReactionValue.isValid(
                    currentPlayer.reaction
                )
            ) {
                return false;
            }

            var previousReaction =
                currentPlayer.reaction;

            /*
             * Pressing the active button again
             * removes the reaction via reaction=0.
             */
            var targetReaction =
                previousReaction ===
                    requestedReaction
                        ? ReactionValue.none
                        : requestedReaction;

            var generation =
                this._generation;

            this._setPending(
                currentPlayer,
                true
            );

            /*
             * Optimistic UI.
             */
            if (
                !this._setReactionState(
                    currentPlayer,
                    targetReaction
                )
            ) {
                this._setPending(
                    currentPlayer,
                    false
                );

                this._log(
                    'CurrentMatchReactionMonitor: WRITE REJECTED' +
                        ' | reason=state-update-failed'
                );

                return false;
            }

            this._log(
                'CurrentMatchReactionMonitor: WRITE' +
                    ' | accountId=' +
                    currentPlayer.accountId +
                    ' | from=' +
                    previousReaction +
                    ' | to=' +
                    targetReaction
            );

            var self =
                this;

            var callbackInvoked =
                false;

            var started =
                this._reactionClient.setReaction(
                    currentPlayer.accountId,
                    targetReaction,

                    function (
                        error,
                        result
                    ) {
                        callbackInvoked =
                            true;

                        self._handleReactionWriteResult(
                            generation,
                            currentPlayer,
                            previousReaction,
                            error,
                            result
                        );
                    }
                );

            if (
                !started &&
                !callbackInvoked
            ) {
                this._handleReactionWriteResult(
                    generation,
                    currentPlayer,
                    previousReaction,

                    {
                        code:
                            'client-not-started',

                        message:
                            'Reaction client did not start the request.'
                    },

                    null
                );
            }

            return started;
        };

    /*
     * accountId identifies the player.
     *
     * Native panel + playerName + heroName
     * additionally protect against a stale
     * UI binding after a lane or hero change.
     */
    CurrentMatchReactionMonitor.prototype._findCurrentStatsPlayer =
        function (expected) {
            if (
                !expected ||
                !this._statsContext
            ) {
                return null;
            }

            for (
                var index = 0;
                index < this._statsContext.players.length;
                index += 1
            ) {
                var current =
                    this._statsContext.players[index];

                if (
                    current.panel === expected.panel &&
                    current.accountId === expected.accountId &&
                    current.playerName === expected.playerName &&
                    current.heroName === expected.heroName
                ) {
                    return current;
                }
            }

            return null;
        };

    CurrentMatchReactionMonitor.prototype._samePlayers =
        function (
            statsPlayers,
            rankPlayers
        ) {
            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var statsPlayer =
                    statsPlayers[index];

                var rankPlayer =
                    rankPlayers[index];

                if (
                    !statsPlayer ||
                    !rankPlayer ||
                    statsPlayer.rosterIndex !== rankPlayer.rosterIndex ||
                    statsPlayer.panel !== rankPlayer.panel ||
                    statsPlayer.playerNameLabel !== rankPlayer.playerNameLabel ||
                    statsPlayer.heroNameLabel !== rankPlayer.heroNameLabel ||
                    statsPlayer.playerName !== rankPlayer.playerName ||
                    statsPlayer.heroName !== rankPlayer.heroName ||
                    statsPlayer.team !== rankPlayer.team ||
                    statsPlayer.teamIndex !== rankPlayer.teamIndex ||
                    (
                        this._statsContext.mode !== 'sandbox' &&
                        statsPlayer.accountIdText !==
                            rankPlayer.accountIdText
                    ) ||
                    statsPlayer.isLocal !==
                        rankPlayer.isLocal
                ) {
                    return false;
                }
            }

            return true;
        };

    CurrentMatchReactionMonitor.prototype._detectMode =
        function (players) {
            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                if (
                    players[index] &&
                    players[index].isSandboxPreview
                ) {
                    return 'sandbox';
                }
            }

            return 'real';
        };

    CurrentMatchReactionMonitor.prototype._isValidPlayers =
        function (players) {
            if (
                !players ||
                players.length !== EXPECTED_PLAYERS
            ) {
                return false;
            }

            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                if (!players[index]) {
                    return false;
                }
            }

            return true;
        };

    CurrentMatchReactionMonitor.prototype._setPending =
        function (
            player,
            pending
        ) {
            try {
                return this._reactionOverlay.setPending(
                    player,
                    pending
                );
            } catch (pendingError) {
                return false;
            }
        };

    CurrentMatchReactionMonitor.prototype._clearOverlay =
        function (reason) {
            try {
                return this._reactionOverlay.clear();
            } catch (clearError) {
                this._log(
                    'CurrentMatchReactionMonitor: CLEAR ERROR' +
                        ' | reason=' +
                        String(reason || '') +
                        ' | error=' +
                        String(clearError)
                );

                return 0;
            }
        };

    /*
     * Stats and rank adapters use one
     * publish/render lifecycle implementation.
     */
    function publishAdapterPlayers(
        adapter,
        players
    ) {
        if (
            adapter._contextKind ===
                'stats'
        ) {
            adapter._reactionMonitor
                .acceptStatsPlayers(
                    players
                );
        } else {
            adapter._reactionMonitor
                .acceptRanksReadyPlayers(
                    players
                );
        }
    }

    function renderAndPublish(
        players
    ) {
        var rendered =
            this._overlay.render(
                players
            );

        if (rendered) {
            publishAdapterPlayers(
                this,
                players
            );
        }

        return rendered;
    }

    function clearAndUnpublish() {
        try {
            return this._overlay.clear();
        } finally {
            publishAdapterPlayers(
                this,
                null
            );
        }
    }

    function initializeAdapter(
        adapter,
        overlay,
        reactionMonitor,
        contextKind
    ) {
        adapter._overlay =
            overlay;

        adapter._reactionMonitor =
            reactionMonitor;

        adapter._contextKind =
            contextKind;
    }

    function CurrentMatchReactionStatsOverlayAdapter(
        statsOverlay,
        reactionMonitor
    ) {
        initializeAdapter(
            this,
            statsOverlay,
            reactionMonitor,
            'stats'
        );
    }

    CurrentMatchReactionStatsOverlayAdapter.prototype.render =
        renderAndPublish;

    CurrentMatchReactionStatsOverlayAdapter.prototype.clear =
        clearAndUnpublish;

    function CurrentMatchReactionRankOverlayAdapter(
        rankOverlay,
        reactionMonitor
    ) {
        initializeAdapter(
            this,
            rankOverlay,
            reactionMonitor,
            'ranks'
        );
    }

    CurrentMatchReactionRankOverlayAdapter.prototype.render =
        renderAndPublish;

    CurrentMatchReactionRankOverlayAdapter.prototype.clear =
        clearAndUnpublish;

    ThreatHud.CurrentMatchReactionMonitor =
        CurrentMatchReactionMonitor;

    ThreatHud.CurrentMatchReactionStatsOverlayAdapter =
        CurrentMatchReactionStatsOverlayAdapter;

    ThreatHud.CurrentMatchReactionRankOverlayAdapter =
        CurrentMatchReactionRankOverlayAdapter;

})(ThreatHud);
