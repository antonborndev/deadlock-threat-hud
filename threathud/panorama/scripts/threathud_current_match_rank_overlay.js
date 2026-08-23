var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var RANK_IMAGE_ID =
        'ThreatHudRankImage';

    var RANK_IMAGE_BASE_URL =
        'http://127.0.0.1:28741/rank-image.png';

    var UiUtils =
        ThreatHud.CurrentMatchUiUtils;

    var isValidPanel =
        UiUtils.isValidPanel;

    function CurrentMatchRankOverlay(
        topPanel,
        logger
    ) {
        /*
         * Kept for a unified interface
         * of overlay components.
         */
        this._topPanel =
            topPanel;

        this._log =
            typeof logger === 'function'
                ? logger
                : function () {};

        /*
         * clear() uses only panels
         * from the last successful render.
         */
        this._renderedPanels =
            [];

        this._renderToken =
            0;
    }

    CurrentMatchRankOverlay.prototype.render =
        function (players) {
            if (
                !UiUtils.validatePlayers(
                    players,
                    EXPECTED_PLAYERS,
                    this._log,
                    'Rank overlay'
                )
            ) {
                return false;
            }

            /*
             * Old images are hidden only
             * after validating the new set.
             */
            this.clear();

            this._renderToken +=
                1;

            var handledCount =
                0;

            var visibleCount =
                0;

            var hiddenCount =
                0;

            for (
                var index = 0;
                index < players.length;
                index += 1
            ) {
                var player =
                    players[index];

                var renderResult =
                    this._renderPlayer(
                        player,
                        this._renderToken
                    );

                if (!renderResult.handled) {
                    continue;
                }

                handledCount +=
                    1;

                if (renderResult.visible) {
                    visibleCount +=
                        1;
                } else {
                    hiddenCount +=
                        1;
                }

                this._rememberPanel(
                    player.panel
                );
            }

            this._log(
                'Rank overlay: render complete' +
                    ' | handled=' +
                    handledCount +
                    '/' +
                    players.length +
                    ' | visible=' +
                    visibleCount +
                    ' | hidden=' +
                    hiddenCount
            );

            return (
                handledCount ===
                    players.length
            );
        };

    CurrentMatchRankOverlay.prototype.clear =
        function () {
            var hiddenCount =
                0;

            for (
                var index = 0;
                index < this._renderedPanels.length;
                index += 1
            ) {
                if (
                    this._hideRankImage(
                        this._renderedPanels[index]
                    )
                ) {
                    hiddenCount +=
                        1;
                }
            }

            this._renderedPanels =
                [];

            if (hiddenCount > 0) {
                this._log(
                    'Rank overlay: CLEAR' +
                        ' | hidden=' +
                        hiddenCount
                );
            }

            return hiddenCount;
        };

    CurrentMatchRankOverlay.prototype._renderPlayer =
        function (
            player,
            renderToken
        ) {
            var playerPanel =
                player.panel;

            if (!isValidPanel(playerPanel)) {
                return {
                    handled:
                        false,

                    visible:
                        false
                };
            }

            var heroImageArea =
                playerPanel.FindChildTraverse(
                    'HeroImageArea'
                );

            if (!isValidPanel(heroImageArea)) {
                this._log(
                    'Rank overlay: HeroImageArea not found' +
                        ' | rosterIndex=' +
                        player.rosterIndex
                );

                return {
                    handled:
                        false,

                    visible:
                        false
                };
            }

            this._configureOverflow(
                playerPanel,
                heroImageArea
            );

            /*
             * One missing rank
             * does not block the other 11.
             */
            if (
                player.status !==
                    'ok'
            ) {
                this._hideRankImage(
                    playerPanel
                );

                this._log(
                    'Rank overlay: rank hidden' +
                        ' | rosterIndex=' +
                        player.rosterIndex +
                        ' | player=' +
                        player.playerName +
                        ' | status=' +
                        player.status
                );

                return {
                    handled:
                        true,

                    visible:
                        false
                };
            }

            if (
                player.rank < 1 ||
                player.rank > 11 ||
                player.subrank < 1 ||
                player.subrank > 6
            ) {
                this._log(
                    'Rank overlay: invalid rank' +
                        ' | rosterIndex=' +
                        player.rosterIndex +
                        ' | rank=' +
                        player.rank +
                        ' | subrank=' +
                        player.subrank
                );

                this._hideRankImage(
                    playerPanel
                );

                return {
                    handled:
                        true,

                    visible:
                        false
                };
            }

            var rankImage =
                heroImageArea.FindChild(
                    RANK_IMAGE_ID
                );

            if (!isValidPanel(rankImage)) {
                rankImage =
                    $.CreatePanel(
                        'Image',
                        heroImageArea,
                        RANK_IMAGE_ID
                    );
            }

            if (!isValidPanel(rankImage)) {
                this._log(
                    'Rank overlay: Image was not created' +
                        ' | rosterIndex=' +
                        player.rosterIndex
                );

                return {
                    handled:
                        false,

                    visible:
                        false
                };
            }

            /*
             * Also called for an existing panel
             * after a VPK update.
             */
            this._configureRankImage(
                rankImage
            );

            var imageUrl =
                this._buildImageUrl(
                    player.rank,
                    player.subrank,
                    renderToken,
                    player.rosterIndex
                );

            try {
                rankImage.SetImage(
                    imageUrl
                );
            } catch (setImageError) {
                rankImage.visible =
                    false;

                this._log(
                    'Rank overlay: SetImage error' +
                        ' | rosterIndex=' +
                        player.rosterIndex +
                        ' | error=' +
                        String(
                            setImageError
                        )
                );

                return {
                    handled:
                        false,

                    visible:
                        false
                };
            }

            rankImage.visible =
                true;

            this._log(
                'Rank overlay: image assigned' +
                    ' | rosterIndex=' +
                    player.rosterIndex +
                    ' | player=' +
                    player.playerName +
                    ' | hero=' +
                    player.heroName +
                    ' | rank=' +
                    player.rank +
                    ' | subrank=' +
                    player.subrank +
                    ' | badge=' +
                    player.badge +
                    ' | sandbox=' +
                    player.isSandboxPreview
            );

            return {
                handled:
                    true,

                visible:
                    true
            };
        };

    CurrentMatchRankOverlay.prototype._configureOverflow =
        function (
            playerPanel,
            heroImageArea
        ) {
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
                /*
                 * The rank can be displayed even without
                 * changing overflow.
                 */
            }
        };

    CurrentMatchRankOverlay.prototype._configureRankImage =
        function (rankImage) {
            rankImage.hittest =
                false;

            rankImage.visible =
                false;

            rankImage.style.width =
                '42px';

            rankImage.style.height =
                '42px';

            rankImage.SetScaling(
                'stretch-to-fit-preserve-aspect'
            );

            rankImage.style.horizontalAlign =
                'center';

            rankImage.style.verticalAlign =
                'bottom';

            rankImage.style.position =
                '0px 0px 0px';

            rankImage.style.zIndex =
                '1100';
        };

    CurrentMatchRankOverlay.prototype._hideRankImage =
        function (playerPanel) {
            if (!isValidPanel(playerPanel)) {
                return false;
            }

            var heroImageArea =
                playerPanel.FindChildTraverse(
                    'HeroImageArea'
                );

            if (!isValidPanel(heroImageArea)) {
                return false;
            }

            var rankImage =
                heroImageArea.FindChild(
                    RANK_IMAGE_ID
                );

            if (!isValidPanel(rankImage)) {
                return false;
            }

            var wasVisible =
                rankImage.visible;

            rankImage.visible =
                false;

            return wasVisible;
        };

    CurrentMatchRankOverlay.prototype._rememberPanel =
        function (panel) {
            if (
                !isValidPanel(panel) ||
                UiUtils.containsPanel(
                    this._renderedPanels,
                    panel
                )
            ) {
                return;
            }

            this._renderedPanels.push(
                panel
            );
        };

    CurrentMatchRankOverlay.prototype._buildImageUrl =
        function (
            rank,
            subrank,
            renderToken,
            rosterIndex
        ) {
            return (
                RANK_IMAGE_BASE_URL +
                '?rank=' +
                rank +
                '&subrank=' +
                subrank +
                '&v=' +
                renderToken +
                '_' +
                rosterIndex
            );
        };

    ThreatHud.CurrentMatchRankOverlay =
        CurrentMatchRankOverlay;

})(ThreatHud);