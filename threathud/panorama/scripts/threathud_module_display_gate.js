var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_PLAYERS =
		12;

	var UiUtils =
		ThreatHud.CurrentMatchUiUtils;

	var ReactionValue =
		ThreatHud.PlayerReactionValue;

	function ModuleDisplayGate(
		overlay,
		moduleName,
		logger
	) {
		this._overlay =
			overlay;

		this._moduleName =
			String(
				moduleName ||
					'Module'
			);

		this._log =
			typeof logger === 'function'
				? logger
				: function () {};

		/*
		 * Fail-closed until the first valid Bridge settings packet.
		 */
		this._enabled =
			false;

		this._players =
			null;
	}

	ModuleDisplayGate.prototype.render =
		function (players) {
			if (
				!UiUtils.validatePlayers(
					players,
					EXPECTED_PLAYERS,
					this._log,
					this._moduleName +
						' display gate'
				)
			) {
				return false;
			}

			this._players =
				players.slice(
					0
				);

			if (!this._enabled) {
				this._clearVisuals();

				/*
				 * Data was accepted even though presentation is hidden.
				 * Existing adapters must still publish Stats/Rank context.
				 */
				return true;
			}

			return this._overlay.render(
				players
			);
		};

	ModuleDisplayGate.prototype.clear =
		function () {
			var cleared =
				this._clearVisuals();

			this._players =
				null;

			return cleared;
		};

	ModuleDisplayGate.prototype.setEnabled =
		function (enabled) {
			var nextEnabled =
				!!enabled;

			if (
				this._enabled ===
					nextEnabled
			) {
				return false;
			}

			this._enabled =
				nextEnabled;

			if (!nextEnabled) {
				this._clearVisuals();
			} else if (this._players) {
				this._overlay.render(
					this._players
				);
			}

			this._log(
				this._moduleName +
					' display gate: ' +
					(
						nextEnabled
							? 'ENABLED'
							: 'DISABLED'
					)
			);

			return true;
		};

	ModuleDisplayGate.prototype.updateReaction =
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

			var updated =
				!this._enabled ||
				!!(
					this._overlay &&
					typeof this._overlay
						.updateReaction ===
							'function' &&
					this._overlay.updateReaction(
						player,
						reaction
					)
				);

			if (updated) {
				this._updateCachedReaction(
					player,
					reaction
				);
			}

			return updated;
		};

	ModuleDisplayGate.prototype._clearVisuals =
		function () {
			if (
				!this._overlay ||
				typeof this._overlay.clear !==
					'function'
			) {
				return 0;
			}

			try {
				var result =
					this._overlay.clear(
						this._players
					);

				return typeof result ===
					'number'
						? result
						: 0;
			} catch (clearError) {
				this._log(
					this._moduleName +
						' display gate: CLEAR ERROR' +
						' | error=' +
						String(
							clearError
						)
				);

				return 0;
			}
		};

	ModuleDisplayGate.prototype._updateCachedReaction =
		function (
			player,
			reaction
		) {
			if (!this._players) {
				return false;
			}

			for (
				var index = 0;
				index < this._players.length;
				index += 1
			) {
				var cached =
					this._players[index];

				if (
					cached &&
					cached.rosterIndex ===
						player.rosterIndex &&
					cached.panel ===
						player.panel &&
					cached.playerName ===
						player.playerName &&
					cached.heroName ===
						player.heroName
				) {
					cached.reaction =
						reaction;

					return true;
				}
			}

			return false;
		};

	ThreatHud.ModuleDisplayGate =
		ModuleDisplayGate;

})(ThreatHud);
