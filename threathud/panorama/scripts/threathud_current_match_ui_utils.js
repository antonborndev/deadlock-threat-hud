var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    function isValidPanel(panel) {
        return !!(
            panel &&
            panel.IsValid()
        );
    }

    function trimText(value) {
        return String(
            value || ''
        ).replace(
            /^\s+|\s+$/g,
            ''
        );
    }

    function containsPanel(
        panels,
        expected
    ) {
        for (
            var index = 0;
            index < panels.length;
            index += 1
        ) {
            if (
                panels[index] ===
                    expected
            ) {
                return true;
            }
        }

        return false;
    }

    function isBindingCurrent(
        player,
        logger,
        sourceName
    ) {
        var log =
            typeof logger === 'function'
                ? logger
                : function () {};

        var prefix =
            String(
                sourceName ||
                'Current match overlay'
            );

        if (
            !player ||
            !isValidPanel(player.panel) ||
            !isValidPanel(player.playerNameLabel) ||
            !isValidPanel(player.heroNameLabel)
        ) {
            log(
                prefix +
                    ': Panorama binding is invalid' +
                    ' | rosterIndex=' +
                    (
                        player
                            ? player.rosterIndex
                            : -1
                    )
            );

            return false;
        }

        var currentPlayerName =
            trimText(
                player.playerNameLabel.text
            );

        var currentHeroName =
            trimText(
                player.heroNameLabel.text
            );

        if (
            currentPlayerName !==
                player.playerName ||
            currentHeroName !==
                player.heroName
        ) {
            log(
                prefix +
                    ': binding is stale' +
                    ' | rosterIndex=' +
                    player.rosterIndex +
                    ' | expectedPlayer=' +
                    player.playerName +
                    ' | currentPlayer=' +
                    currentPlayerName +
                    ' | expectedHero=' +
                    player.heroName +
                    ' | currentHero=' +
                    currentHeroName
            );

            return false;
        }

        return true;
    }

    /*
     * Single validation of the full player array
     * before changing dynamic overlays.
     *
     * It simultaneously guarantees:
     *
     * - the expected number of entries;
     * - current native bindings;
     * - no duplicate panel assignment.
     */
    function validatePlayers(
        players,
        expectedCount,
        logger,
        sourceName
    ) {
        var log =
            typeof logger === 'function'
                ? logger
                : function () {};

        var prefix =
            String(
                sourceName ||
                'Current match overlay'
            );

        if (
            !players ||
            players.length !==
                expectedCount
        ) {
            log(
                prefix +
                    ': invalid data count' +
                    ' | players=' +
                    (
                        players
                            ? players.length
                            : 0
                    )
            );

            return false;
        }

        var usedPanels =
            [];

        for (
            var index = 0;
            index < players.length;
            index += 1
        ) {
            var player =
                players[index];

            if (
                !isBindingCurrent(
                    player,
                    log,
                    prefix
                )
            ) {
                return false;
            }

            if (
                containsPanel(
                    usedPanels,
                    player.panel
                )
            ) {
                log(
                    prefix +
                        ': one panel is assigned to two players' +
                        ' | rosterIndex=' +
                        player.rosterIndex
                );

                return false;
            }

            usedPanels.push(
                player.panel
            );
        }

        return true;
    }

    ThreatHud.CurrentMatchUiUtils = {
        isValidPanel:
            isValidPanel,

        containsPanel:
            containsPanel,

        isBindingCurrent:
            isBindingCurrent,

        validatePlayers:
            validatePlayers
    };

})(ThreatHud);