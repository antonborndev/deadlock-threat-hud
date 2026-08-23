var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function EnemyTeamMonitor(
		enemyTeam,
		logger
	) {
		this._enemyTeam =
			enemyTeam;

		this._log =
			typeof logger ===
				'function'
				? logger
				: function () {};

		this._running =
			false;

		this._retryInterval =
			5.0;

		this._expectedPlayers =
			6;
	}

	EnemyTeamMonitor.prototype
		._scheduleRetry =
		function () {
			if (!this._running) {
				return;
			}

			var self = this;

			$.Schedule(
				this._retryInterval,
				function () {
					self._attempt();
				}
			);
		};

	EnemyTeamMonitor.prototype
		._attempt =
		function () {
			if (!this._running) {
				return;
			}

			var connected =
				this._enemyTeam.connect();

			if (!connected) {
				this._scheduleRetry();
				return;
			}

			var playerPanels =
				this._enemyTeam
					.getPlayerPanels();

			if (
				playerPanels.length <
				this._expectedPlayers
			) {
				this._log(
					'EnemyTeam: player panels: ' +
					playerPanels.length
				);

				this._scheduleRetry();
				return;
			}

			if (
				!this._enemyTeam
					.readPlayers()
			) {
				/*
				 * The panels already exist,
				 * but the text is not populated yet.
				 */
				this._scheduleRetry();
				return;
			}

			var players =
				this._enemyTeam
					.getPlayers();

			for (
				var i = 0;
				i < players.length;
				i++
			) {
				this._log(
					(i + 1) +
						'. ' +
						players[i].playerName +
						' — ' +
						players[i].heroName
				);
			}

			this._log(
				'EnemyTeam: all players saved, ' +
				'search stopped'
			);

			this._running = false;
		};

	EnemyTeamMonitor.prototype.start =
		function () {
			if (this._running) {
				return false;
			}

			this._running = true;
			this._attempt();

			return true;
		};

	EnemyTeamMonitor.prototype.stop =
		function () {
			this._running = false;
		};

	ThreatHud.EnemyTeamMonitor =
		EnemyTeamMonitor;

})(ThreatHud);