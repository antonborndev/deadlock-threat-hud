var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var PLAYERS_PER_TEAM =
        6;

    var OVERLAY_ID =
        'ThreatHudStatsOverlay';

    var LEGACY_OVERLAY_PREFIX =
        'ThreatHudStatsOverlay_';

    var REACTION_IMAGE_ID =
        'ThreatHudStatsReactionImage';

    var OVERLAY_WIDTH = 72;
    var OVERLAY_HEIGHT = 24;

    var ReactionValue =
        ThreatHud.PlayerReactionValue;

    var ReactionVisuals =
        ThreatHud.ReactionVisuals;

    var UiUtils =
        ThreatHud.CurrentMatchUiUtils;

    var isValidPanel =
        UiUtils.isValidPanel;

    function CurrentMatchStatsOverlay(
        topPanel,
        logger
    ) {
        this._topPanel =
            topPanel;

        this._log =
            typeof logger === 'function'
                ? logger
                : function () {};
    }

    CurrentMatchStatsOverlay.prototype.render =
        function (players) {
            if (
                !UiUtils.validatePlayers(
                    players,
                    EXPECTED_PLAYERS,
                    this._log,
                    'Stats overlay'
                )
            ) {
                return false;
            }

            this.clear(
                players
            );

            var renderedCount =
                0;

            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                var player =
                    players[index];

                if (
                    this._renderPlayer(
                        player.panel,
                        player
                    )
                ) {
                    renderedCount += 1;
                }
            }

            this._log(
                'Stats overlay: render complete' +
                    ' | rendered=' +
                    renderedCount +
                    '/' +
                    players.length
            );

            return (
                renderedCount ===
                    players.length
            );
        };

    CurrentMatchStatsOverlay.prototype.clear =
        function (players) {
            if (!players) {
                return 0;
            }

            var clearedCount =
                0;

            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                if (
                    players[index] &&
                    isValidPanel(
                        players[index].panel
                    )
                ) {
                    this._hideExistingOverlays(
                        players[index].panel
                    );

                    clearedCount +=
                        1;
                }
            }

            return clearedCount;
        };

    /*
     * Instantly updates only the reaction image
     * inside the existing winrate badge.
     *
     * No new stats request, matching, or roster
     * search is performed.
     */
    CurrentMatchStatsOverlay.prototype.updateReaction =
        function (
            player,
            reaction
        ) {
            if (
                !UiUtils.isBindingCurrent(
                    player,
                    this._log,
                    'Stats overlay'
                ) ||
                !ReactionValue.isValid(
                    reaction
                )
            ) {
                return false;
            }

            var reactionImage =
                this._findReactionImage(
                    player.panel
                );

            if (!isValidPanel(reactionImage)) {
                return false;
            }

            return ReactionVisuals.applyStatusImage(
                reactionImage,
                reaction
            );
        };

    CurrentMatchStatsOverlay.prototype._hideExistingOverlays =
        function (playerPanel) {
            if (!isValidPanel(playerPanel)) {
                return;
            }

            var heroImageArea =
                playerPanel.FindChildTraverse(
                    'HeroImageArea'
                );

            if (!isValidPanel(heroImageArea)) {
                return;
            }

            this._hideOverlayById(
                heroImageArea,
                OVERLAY_ID
            );

            for (
                var teamIndex = 0;
                teamIndex < PLAYERS_PER_TEAM;
                teamIndex += 1
            ) {
                this._hideOverlayById(
                    heroImageArea,
                    LEGACY_OVERLAY_PREFIX +
                        'ally_' +
                        teamIndex
                );

                this._hideOverlayById(
                    heroImageArea,
                    LEGACY_OVERLAY_PREFIX +
                        'enemy_' +
                        teamIndex
                );
            }

            for (
                var rosterIndex = 0;
                rosterIndex < EXPECTED_PLAYERS;
                rosterIndex += 1
            ) {
                this._hideOverlayById(
                    heroImageArea,
                    LEGACY_OVERLAY_PREFIX +
                        rosterIndex
                );
            }
        };

    CurrentMatchStatsOverlay.prototype._hideOverlayById =
        function (
            parent,
            overlayId
        ) {
            var overlay =
                parent.FindChild(
                    overlayId
                );

            if (isValidPanel(overlay)) {
                overlay.visible =
                    false;
            }
        };

    CurrentMatchStatsOverlay.prototype._renderPlayer =
        function (
            playerPanel,
            player
        ) {
            if (!isValidPanel(playerPanel)) {
                return false;
            }

            var heroImageArea =
                playerPanel.FindChildTraverse(
                    'HeroImageArea'
                );

            if (!isValidPanel(heroImageArea)) {
                this._log(
                    'Stats overlay: HeroImageArea not found' +
                        ' | rosterIndex=' +
                        player.rosterIndex
                );

                return false;
            }

            /*
             * No Steam identity means no badge.
             * The slot is considered processed and does not
             * block the other players.
             */
            if (
                player.status ===
                    'identity-unresolved'
            ) {
                this._hideOverlayById(
                    heroImageArea,
                    OVERLAY_ID
                );

                return true;
            }

            var heroContents =
                playerPanel.FindChildTraverse(
                    'HeroContents'
                );

            try {
                playerPanel.style.overflow =
                    'noclip';

                if (isValidPanel(heroContents)) {
                    heroContents.style.overflow =
                        'noclip';
                }

                heroImageArea.style.overflow =
                    'noclip';
            } catch (overflowError) {
            }

            var overlay =
                heroImageArea.FindChild(
                    OVERLAY_ID
                );

            if (!isValidPanel(overlay)) {
                overlay =
                    $.CreatePanel(
                        'Panel',
                        heroImageArea,
                        OVERLAY_ID
                    );
            }

            if (!isValidPanel(overlay)) {
                return false;
            }

            var isEnemy =
                player.rosterIndex >= PLAYERS_PER_TEAM &&
                player.rosterIndex < EXPECTED_PLAYERS;

            var horizontalAlign =
                isEnemy ? 'right' : 'left';

            this._configureOverlay(
                overlay,
                isEnemy
            );

            var gamesLabel =
                this._getOrCreateLabel(
                    overlay,
                    OVERLAY_ID +
                        '_Games'
                );

            var winRateLabel =
                this._getOrCreateLabel(
                    overlay,
                    OVERLAY_ID +
                        '_WinRate'
                );

            var reactionImage =
                ReactionVisuals.getOrCreateImage(
                    overlay,
                    REACTION_IMAGE_ID,
                    ReactionValue.like,
                    10
                );

            if (
                !isValidPanel(gamesLabel) ||
                !isValidPanel(winRateLabel) ||
                !isValidPanel(reactionImage)
            ) {
                return false;
            }

            this._configureTextLabel(
                gamesLabel,
                {
                    maxWidth:
                        '52px',

                    height:
                        '13px',

                    fontSize:
                        '10px',

                    horizontalAlign:
                        horizontalAlign,

                    position:
                        '0px 10px 0px',

                    color:
                        '#FFFFFF'
                }
            );

            this._configureTextLabel(
                winRateLabel,
                {
                    maxWidth:
                        '72px',

                    height:
                        '16px',

                    fontSize:
                        '12px',

                    horizontalAlign:
                        horizontalAlign,

                    position:
                        '0px 0px 0px',

                    color:
                        '#FFFFFF'
                }
            );

            reactionImage.style.horizontalAlign =
                'left';

            reactionImage.style.verticalAlign =
                'top';

            reactionImage.style.position =
                (isEnemy ? '6px' : '56px') + ' 11px 0px';

            reactionImage.style.zIndex =
                '1001';

            gamesLabel.text =
                this._formatGames(
                    player
                );

            if (
                !ReactionVisuals.applyStatusImage(
                    reactionImage,
                    player.reaction
                )
            ) {
                return false;
            }

            winRateLabel.text =
                this._formatWinRate(
                    player
                );

            winRateLabel.style.color =
                this._getWinRateColor(
                    player
                );

            overlay.visible =
                true;

            this._log(
                'Stats overlay: player rendered' +
                    ' | rosterIndex=' +
                    player.rosterIndex +
                    ' | player=' +
                    player.playerName +
                    ' | hero=' +
                    player.heroName +
                    ' | games=' +
                    player.matchesPlayed +
                    ' | winrate=' +
                    player.winRatePercent.toFixed(1) +
                    ' | reaction=' +
                    String(player.reaction)
            );

            return true;
        };

    CurrentMatchStatsOverlay.prototype._getOrCreateLabel =
        function (
            parent,
            id
        ) {
            var label =
                parent.FindChild(
                    id
                );

            if (!isValidPanel(label)) {
                label =
                    $.CreatePanel(
                        'Label',
                        parent,
                        id
                    );
            }

            return isValidPanel(label)
                ? label
                : null;
        };

    CurrentMatchStatsOverlay.prototype._configureOverlay =
        function (overlay, isEnemy) {
            overlay.hittest =
                false;

            overlay.visible =
                true;

            overlay.style.width =
                OVERLAY_WIDTH + 'px';

            overlay.style.height =
                OVERLAY_HEIGHT + 'px';

            overlay.style.flowChildren =
                'none';

            overlay.style.horizontalAlign =
                isEnemy ? 'right' : 'left';

            overlay.style.verticalAlign =
                'top';

            // Align visible glyph edges to the black health-bar border.
            // Account for rotation when matching the two visible vertical gaps.
            overlay.style.position =
                isEnemy ? '7px 10px 0px' : '1.5px 10.5px 0px';

            // Mirror the layout around the health-bar side, keeping text readable.
            overlay.style.transformOrigin =
                isEnemy ? '100% 0%' : '0% 0%';

            overlay.style.transform =
                isEnemy ? 'rotateZ(9deg)' : 'rotateZ(-11deg)';

            overlay.style.overflow =
                'noclip';

            overlay.style.backgroundColor =
                '#00000000';

            overlay.style.border =
                '0px solid #00000000';

            overlay.style.borderRadius =
                '0px';

            overlay.style.zIndex =
                '1000';

            // A reused panel may still contain the previous textured backdrop.
            var oldBackdrop = overlay.FindChild('ThreatHudStatsBackdrop');
            if (isValidPanel(oldBackdrop)) {
                oldBackdrop.visible = false;
            }
        };

    CurrentMatchStatsOverlay.prototype._configureTextLabel =
        function (
            label,
            options
        ) {
            label.hittest =
                false;

            label.style.width =
                'fit-children';

            label.style.maxWidth =
                options.maxWidth;

            label.style.height =
                options.height;

            label.style.fontSize =
                options.fontSize;

            label.style.fontWeight =
                'bold';

            label.style.fontFamily =
                'VALVEOracle';

            label.style.color =
                options.color;

            label.style.textAlign =
                options.horizontalAlign;

            label.style.horizontalAlign =
                options.horizontalAlign;

            label.style.verticalAlign =
                'top';

            label.style.position =
                options.position;

            label.style.textOverflow =
                'shrink';

            label.style.whiteSpace =
                'nowrap';

            label.style.textShadow =
                '0px 0px 2px 3.0 #000000';

            label.style.zIndex =
                '1';
        };

    CurrentMatchStatsOverlay.prototype._findReactionImage =
        function (playerPanel) {
            if (!isValidPanel(playerPanel)) {
                return null;
            }

            var heroImageArea =
                playerPanel.FindChildTraverse(
                    'HeroImageArea'
                );

            if (!isValidPanel(heroImageArea)) {
                return null;
            }

            var overlay =
                heroImageArea.FindChild(
                    OVERLAY_ID
                );

            if (!isValidPanel(overlay)) {
                return null;
            }

            return overlay.FindChild(
                REACTION_IMAGE_ID
            );
        };

    CurrentMatchStatsOverlay.prototype._formatGames =
        function (player) {
            if (
                player.status ===
                    'ok'
            ) {
                return String(
                    player.matchesPlayed
                ) + ' M';
            }

            if (
                player.status ===
                    'stats-not-found'
            ) {
                return '0 M';
            }

            return '? M';
        };

    CurrentMatchStatsOverlay.prototype._formatWinRate =
        function (player) {
            if (
                player.status ===
                    'ok'
            ) {
                return (
                    player.winRatePercent.toFixed(1) +
                    '%'
                );
            }

            return '—';
        };

    CurrentMatchStatsOverlay.prototype._getWinRateColor =
        function (player) {
            if (
                player.status !==
                    'ok'
            ) {
                return '#D6C06E';
            }

            if (
                player.winRatePercent >=
                    55
            ) {
                return '#00A704';
            }

            if (
                player.winRatePercent <
                    45
            ) {
                return '#FF0000';
            }

            return '#FFFFFF';
        };

    ThreatHud.CurrentMatchStatsOverlay =
        CurrentMatchStatsOverlay;

})(ThreatHud);
