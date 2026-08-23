var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function EnemyTeam(topBarPanel, logger) {
		this._topBarPanel = topBarPanel;
		this._teamPanel = null;
		this._playersContainer = null;
        this._players = [];
        this._log =
		typeof logger === 'function'
			? logger
			: function () {};
	}

    
    function findPanelsByType(rootPanel, panelType) {
        var result = [];

        if (!rootPanel || !rootPanel.IsValid()) {
            return result;
        }

        var queue = [rootPanel];

        for (var i = 0; i < queue.length; i++) {
            var panel = queue[i];

            if (!panel || !panel.IsValid()) {
                continue;
            }

            if (
                panel !== rootPanel &&
                panel.paneltype === panelType
            ) {
                result.push(panel);
            }

            var children = panel.Children();

            for (var j = 0; j < children.length; j++) {
                queue.push(children[j]);
            }
        }

        return result;
    }

    function findFirstLabelByClass(rootPanel, className) {
        if (!rootPanel || !rootPanel.IsValid()) {
            return null;
        }

        var matches =
            rootPanel.FindChildrenWithClassTraverse(className);

        for (var i = 0; i < matches.length; i++) {
            if (
                matches[i] &&
                matches[i].IsValid() &&
                matches[i].paneltype === 'Label'
            ) {
                return matches[i];
            }
        }

        return null;
    }

    // Private helper search function.
    function findPanelBySignature(
        rootPanel,
        requiredType,
        allowedIds,
        requiredClass
    ) {
        if (!rootPanel || !rootPanel.IsValid()) {
            return null;
        }

        var queue = [rootPanel];

        for (var i = 0; i < queue.length; i++) {
            var panel = queue[i];

            if (!panel || !panel.IsValid()) {
                continue;
            }

            var typeMatches =
                !requiredType ||
                panel.paneltype === requiredType;

            var idMatches =
                !allowedIds ||
                allowedIds.indexOf(panel.id) !== -1;

            var classMatches =
                !requiredClass ||
                panel.BHasClass(requiredClass);

            if (
                typeMatches &&
                idMatches &&
                classMatches
            ) {
                return panel;
            }

            var children = panel.Children();

            for (var j = 0; j < children.length; j++) {
                queue.push(children[j]);
            }
        }

        return null;
    }

    EnemyTeam.prototype.getItemImagePanels = function (playerPanel) {
        var result = [];

        if (!playerPanel || !playerPanel.IsValid()) {
            return result;
        }

        var modsContainer =
            playerPanel.FindChildTraverse(
                'PlayerModsContainer'
            );

        if (!modsContainer || !modsContainer.IsValid()) {
            this._log(
                'EnemyTeam: PlayerModsContainer not found'
            );

            return result;
        }

        var itemSlots =
            findPanelsByType(
                modsContainer,
                'CitadelModIcon'
            );

        for (var i = 0; i < itemSlots.length; i++) {
            var slotPanel = itemSlots[i];

            /*
            * The search is performed relative to a specific
            * CitadelModIcon, not the entire player panel.
            */
            var imagePanel =
                slotPanel.FindChildTraverse(
                    'ModIconImage'
                );

            if (!imagePanel || !imagePanel.IsValid()) {
                continue;
            }

            result.push({
                slotIndex: i,
                slotId: slotPanel.id || '',
                slotPanel: slotPanel,
                imagePanel: imagePanel
            });
        }

        return result;
    };

    EnemyTeam.prototype.readPlayers = function () {
        var playerPanels = this.getPlayerPanels();

        if (playerPanels.length < 6) {
            this._log(
                'EnemyTeam: found only ' +
                playerPanels.length +
                ' player panels'
            );

            return false;
        }

        var foundPlayers = [];

        for (var i = 0; i < playerPanels.length; i++) {
            var playerPanel = playerPanels[i];

            var playerNameLabel =
                findFirstLabelByClass(
                    playerPanel,
                    'PlayerName'
                );

            var heroNameLabel =
                findFirstLabelByClass(
                    playerPanel,
                    'HeroName'
                );

            if (!playerNameLabel || !heroNameLabel) {
                this._log(
                    'EnemyTeam: names not found for player #' +
                    (i + 1)
                );

                return false;
            }

            var playerName =
                String(playerNameLabel.text || '');

            var heroName =
                String(heroNameLabel.text || '');

            if (!playerName || !heroName) {
                this._log(
                    'EnemyTeam: names are not populated yet for player #' +
                    (i + 1)
                );

                return false;
            }

            foundPlayers.push({
                index: i,
                playerName: playerName,
                heroName: heroName
            });
        }
        /*
        * Replace internal state only after
        * all six players have been read successfully.
        */
        this._players = foundPlayers;

        this._log(
            'EnemyTeam: saved names of ' +
            this._players.length +
            ' players'
        );

        return true;
    };

    EnemyTeam.prototype.getPlayers = function () {
        var result = [];

        for (var i = 0; i < this._players.length; i++) {
            result.push({
                index: this._players[i].index,
                playerName: this._players[i].playerName,
                heroName: this._players[i].heroName
            });
        }

        return result;
    };

   EnemyTeam.prototype.connect = function () {
        if (
            !this._topBarPanel ||
            !this._topBarPanel.IsValid()
        ) {
            this._log(
                'EnemyTeam: root TopBar panel is unavailable'
            );

            return false;
        }

        this._teamPanel = null;
        this._playersContainer = null;

        this._teamPanel = findPanelBySignature(
            this._topBarPanel,
            'CitadelHudTopBarTeam',
            [
                'TeamFriendly',
                'TeamEnemy'
            ],
            'enemy'
        );

        if (
            !this._teamPanel ||
            !this._teamPanel.IsValid()
        ) {
            this._log(
                'EnemyTeam: panel of type CitadelHudTopBarTeam ' +
                'with ID TeamFriendly/TeamEnemy and enemy class was not found'
            );

            return false;
        }

        this._log(
            'EnemyTeam: panel found' +
            ' | id=' + (this._teamPanel.id || '<empty>') +
            ' | type=' +
            (this._teamPanel.paneltype || '<empty>') +
            ' | enemy=' +
            this._teamPanel.BHasClass('enemy') +
            ' | friend=' +
            this._teamPanel.BHasClass('friend')
        );

        this._playersContainer =
            this._teamPanel.FindChildTraverse(
                'PlayersContainer'
            );

        if (
            !this._playersContainer ||
            !this._playersContainer.IsValid()
        ) {
            this._log(
                'EnemyTeam: PlayersContainer not found'
            );

            return false;
        }

        this._log(
            'EnemyTeam: connected players: ' +
            this._playersContainer.Children().length
        );

        return true;
    };

	EnemyTeam.prototype.getTeamPanel = function () {
		if (
			!this._teamPanel ||
			!this._teamPanel.IsValid()
		) {
			return null;
		}

		return this._teamPanel;
	};

	EnemyTeam.prototype.getPlayersContainer = function () {
		if (
			!this._playersContainer ||
			!this._playersContainer.IsValid()
		) {
			return null;
		}

		return this._playersContainer;
	};

	EnemyTeam.prototype.getPlayerPanels = function () {
		var container =
			this.getPlayersContainer();

		if (!container) {
			return [];
		}

		return container.Children();
	};

	ThreatHud.EnemyTeam = EnemyTeam;

})(ThreatHud);

