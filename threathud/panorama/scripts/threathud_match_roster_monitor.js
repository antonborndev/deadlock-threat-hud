var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function MatchRosterMonitor(
		matchRoster,
		logger
	) {
		this._matchRoster =
			matchRoster;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._running =
			false;

		this._retryInterval =
			1.0;

		this._lastFingerprint =
			'';

		this._stableAttempts =
			0;

		this._requiredStableAttempts =
			2;
	}

	MatchRosterMonitor.prototype
		._resetStability =
		function () {
			this._lastFingerprint =
				'';

			this._stableAttempts =
				0;
		};

	MatchRosterMonitor.prototype
		._scheduleRetry =
		function () {
			if (!this._running) {
				return;
			}

			var self =
				this;

			$.Schedule(
				this._retryInterval,
				function () {
					self._attempt();
				}
			);
		};

	MatchRosterMonitor.prototype
		._logPlayers =
		function () {
			var players =
				this._matchRoster
					.getPlayers();

			this._log(
				'MatchRoster: stable roster saved' +
					' | players=' +
					players.length
			);

			for (
				var index = 0;
				index < players.length;
				index++
			) {
				var player =
					players[index];

				this._log(
					'Roster [' +
						player.rosterIndex +
						']' +
						' | team=' +
						player.team +
						' | teamIndex=' +
						player.teamIndex +
						' | player=' +
						player.playerName +
						' | hero=' +
						player.heroName
				);
			}
		};

	MatchRosterMonitor.prototype
		._attempt =
		function () {
			if (!this._running) {
				return;
			}

			if (
				!this._matchRoster.connect()
			) {
				this._resetStability();
				this._scheduleRetry();

				return;
			}

			if (
				!this._matchRoster
					.readPlayers()
			) {
				this._resetStability();
				this._scheduleRetry();

				return;
			}

			var fingerprint =
				this._matchRoster
					.getFingerprint();

			if (
				fingerprint ===
					this._lastFingerprint
			) {
				this._stableAttempts++;
			} else {
				this._lastFingerprint =
					fingerprint;

				this._stableAttempts =
					1;
			}

			if (
				this._stableAttempts <
					this._requiredStableAttempts
			) {
				this._log(
					'MatchRoster: roster found, ' +
						'checking stability' +
						' | attempt=' +
						this._stableAttempts +
						'/' +
						this._requiredStableAttempts
				);

				this._scheduleRetry();

				return;
			}

			this._logPlayers();

			this._running =
				false;
		};

	MatchRosterMonitor.prototype.start =
		function () {
			if (this._running) {
				return false;
			}

			this._resetStability();

			this._running =
				true;

			this._attempt();

			return true;
		};

	MatchRosterMonitor.prototype.stop =
		function () {
			this._running =
				false;

			this._resetStability();
		};

	ThreatHud.MatchRosterMonitor =
		MatchRosterMonitor;

})(ThreatHud);