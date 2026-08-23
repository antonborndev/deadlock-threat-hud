var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var POLL_INTERVAL =
		0.10;

	var MAX_ATTEMPTS =
		50;

	var PANEL_DELETE_DELAY =
		0.10;

	var NEXT_PLAYER_DELAY =
		0.25;

	function SteamIdentityResolver(
		context,
		logger
	) {
		this._context =
			context;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._generation =
			1;

		this._activePanel =
			null;

		this._running =
			false;
	}

	SteamIdentityResolver.prototype.resolve =
		function (
			bridgePlayers,
			callback
		) {
			if (this._running) {
				this._invokeCallback(
					callback,

					this._createError(
						'already-running',

						'Steam identity resolver is already running.',

						null
					),

					null
				);

				return false;
			}

			if (
				!bridgePlayers ||
				bridgePlayers.length === 0
			) {
				this._invokeCallback(
					callback,
					null,
					[]
				);

				return true;
			}

			this._running =
				true;

			this._generation++;

			var generation =
				this._generation;

			this._deleteActivePanel();

			this._log(
				'SteamIdentityResolver: START' +
					' | players=' +
					bridgePlayers.length
			);

			this._resolveNext(
				bridgePlayers,
				0,
				[],
				generation,
				callback
			);

			return true;
		};

	SteamIdentityResolver.prototype
		._resolveNext =
		function (
			bridgePlayers,
			index,
			results,
			generation,
			callback
		) {
			if (
				generation !==
					this._generation
			) {
				return;
			}

			if (
				index >=
					bridgePlayers.length
			) {
				this._running =
					false;

				this._activePanel =
					null;

				this._log(
					'SteamIdentityResolver: DONE' +
						' | resolved=' +
						this._countResolved(
							results
						) +
						'/' +
						results.length
				);

				this._invokeCallback(
					callback,
					null,
					results
				);

				return;
			}

			var bridgePlayer =
				bridgePlayers[index];

			var panel =
				this._createPanel(
					bridgePlayer,
					index
				);

			if (!panel) {
				this._running =
					false;

				this._invokeCallback(
					callback,

					this._createError(
						'steam-name-panel-error',

						'Failed to create CitadelUserName panel.',

						'index=' +
							index +
							', accountID=' +
							bridgePlayer.accountIdText
					),

					null
				);

				return;
			}

			this._activePanel =
				panel;

			this._log(
				'SteamIdentityResolver: REQUEST' +
					' | index=' +
					index +
					'/' +
					bridgePlayers.length +
					' | accountID=' +
					bridgePlayer.accountIdText
			);

			this._pollCurrentPlayer(
				bridgePlayers,
				index,
				results,
				panel,
				generation,
				1,
				callback
			);
		};

	SteamIdentityResolver.prototype
		._pollCurrentPlayer =
		function (
			bridgePlayers,
			index,
			results,
			panel,
			generation,
			attempt,
			callback
		) {
			if (
				generation !==
					this._generation
			) {
				return;
			}

			var bridgePlayer =
				bridgePlayers[index];

			var steamName =
				this._readName(
					panel
				);

			if (steamName !== '') {
				var result =
					this._createResolvedResult(
						bridgePlayer,
						panel,
						steamName
					);

				results.push(
					result
				);

				this._log(
					'SteamIdentityResolver: RESOLVED' +
						' | index=' +
						index +
						' | accountID=' +
						result.accountIdText +
						' | steamName=' +
						result.steamName +
						' | local=' +
						result.isLocal
				);

				this._retirePanelAndContinue(
					bridgePlayers,
					index,
					results,
					panel,
					generation,
					callback
				);

				return;
			}

			if (
				attempt >=
					MAX_ATTEMPTS
			) {
				results.push(
					this._createUnresolvedResult(
						bridgePlayer
					)
				);

				this._log(
					'SteamIdentityResolver: TIMEOUT' +
						' | index=' +
						index +
						' | accountID=' +
						bridgePlayer.accountIdText
				);

				this._retirePanelAndContinue(
					bridgePlayers,
					index,
					results,
					panel,
					generation,
					callback
				);

				return;
			}

			var self =
				this;

			$.Schedule(
				POLL_INTERVAL,

				function () {
					self._pollCurrentPlayer(
						bridgePlayers,
						index,
						results,
						panel,
						generation,
						attempt + 1,
						callback
					);
				}
			);
		};

	SteamIdentityResolver.prototype
		._retirePanelAndContinue =
		function (
			bridgePlayers,
			index,
			results,
			panel,
			generation,
			callback
		) {
			if (
				this._activePanel ===
					panel
			) {
				this._activePanel =
					null;
			}

			this._deletePanel(
				panel,
				PANEL_DELETE_DELAY
			);

			var self =
				this;

			$.Schedule(
				NEXT_PLAYER_DELAY,

				function () {
					self._resolveNext(
						bridgePlayers,
						index + 1,
						results,
						generation,
						callback
					);
				}
			);
		};

	SteamIdentityResolver.prototype
		._createPanel =
		function (
			bridgePlayer,
			index
		) {
			var panel =
				null;

			try {
				panel =
					$.CreatePanel(
						'CitadelUserName',
						this._context,

						'ThreatHudSteamIdentity_' +
							index +
							'_' +
							String(
								Date.now()
							)
					);
			} catch (createError) {
				this._log(
					'SteamIdentityResolver: CREATE ERROR' +
						' | index=' +
						index +
						' | error=' +
						String(
							createError
						)
				);

				return null;
			}

			if (!panel) {
				return null;
			}

			try {
				panel.hittest =
					false;

				panel.visible =
					true;

				panel.style.width =
					'fit-children';

				panel.style.height =
					'fit-children';

				panel.style.opacity =
					'0.01';

				panel.style.horizontalAlign =
					'left';

				panel.style.verticalAlign =
					'top';

				panel.style.marginLeft =
					'1px';

				panel.style.marginTop =
					'1px';

				panel.accountid =
					bridgePlayer.accountIdText;
			} catch (assignmentError) {
				this._log(
					'SteamIdentityResolver: ASSIGN ERROR' +
						' | index=' +
						index +
						' | error=' +
						String(
							assignmentError
						)
				);

				this._deletePanel(
					panel,
					0.0
				);

				return null;
			}

			return panel;
		};

	SteamIdentityResolver.prototype
		._createResolvedResult =
		function (
			bridgePlayer,
			panel,
			steamName
		) {
			return {
				accountId:
					bridgePlayer.accountId,

				accountIdText:
					bridgePlayer.accountIdText,

				isLocal:
					bridgePlayer.isLocal,

				steamId64:
					this._readProperty(
						panel,
						'steamid'
					),

				steamName:
					steamName,

				normalizedSteamName:
					ThreatHud
						.IdentityUtils
						.normalizeName(
							steamName
						),

				nameResolved:
					true
			};
		};

	SteamIdentityResolver.prototype
		._createUnresolvedResult =
		function (bridgePlayer) {
			return {
				accountId:
					bridgePlayer.accountId,

				accountIdText:
					bridgePlayer.accountIdText,

				isLocal:
					bridgePlayer.isLocal,

				steamId64:
					'',

				steamName:
					'',

				normalizedSteamName:
					'',

				nameResolved:
					false
			};
		};

	SteamIdentityResolver.prototype
		._readName =
		function (panel) {
			if (
				!panel ||
				!panel.IsValid()
			) {
				return '';
			}

			/*
			 * Some native panels may
			 * provide text directly.
			 */
			try {
				var directText =
					String(
						panel.text ||
							''
					).replace(
						/^\s+|\s+$/g,
						''
					);

				if (directText !== '') {
					return directText;
				}
			} catch (directTextError) {
				/*
				 * Continue traversing child Labels.
				 */
			}

			var queue =
				[
					panel
				];

			for (
				var index = 0;
				index < queue.length;
				index++
			) {
				var current =
					queue[index];

				if (
					!current ||
					!current.IsValid()
				) {
					continue;
				}

				if (
					current.paneltype ===
						'Label'
				) {
					var text =
						String(
							current.text ||
								''
						).replace(
							/^\s+|\s+$/g,
							''
						);

					if (text !== '') {
						return text;
					}
				}

				var children =
					current.Children();

				for (
					var childIndex = 0;
					childIndex <
						children.length;
					childIndex++
				) {
					queue.push(
						children[
							childIndex
						]
					);
				}
			}

			return '';
		};

	SteamIdentityResolver.prototype
		._readProperty =
		function (
			panel,
			propertyName
		) {
			try {
				return String(
					panel[
						propertyName
					] ||
						''
				);
			} catch (propertyError) {
				return '';
			}
		};

	SteamIdentityResolver.prototype
		._countResolved =
		function (results) {
			var count =
				0;

			for (
				var index = 0;
				index < results.length;
				index++
			) {
				if (
					results[index]
						.nameResolved
				) {
					count++;
				}
			}

			return count;
		};

	SteamIdentityResolver.prototype
		._deleteActivePanel =
		function () {
			if (!this._activePanel) {
				return;
			}

			this._deletePanel(
				this._activePanel,
				0.0
			);

			this._activePanel =
				null;
		};

	SteamIdentityResolver.prototype
		._deletePanel =
		function (
			panel,
			delay
		) {
			if (!panel) {
				return;
			}

			try {
				if (
					typeof panel.IsValid ===
						'function' &&
					!panel.IsValid()
				) {
					return;
				}
			} catch (validationError) {
				return;
			}

			try {
				panel.DeleteAsync(
					delay
				);
			} catch (deleteError) {
				/*
				 * The panel may already have been removed.
				 */
			}
		};

	SteamIdentityResolver.prototype.dispose =
		function () {
			this._generation++;
			this._running = false;
			this._deleteActivePanel();
		};

	SteamIdentityResolver.prototype
		._createError =
		function (
			code,
			message,
			detail
		) {
			return {
				code:
					String(
						code ||
							'unknown-error'
					),

				message:
					String(
						message ||
							''
					),

				detail:
					detail ===
						undefined
							? null
							: detail
			};
		};

	SteamIdentityResolver.prototype
		._invokeCallback =
		function (
			callback,
			error,
			result
		) {
			if (
				typeof callback ===
					'function'
			) {
				callback(
					error || null,
					result || null
				);
			}
		};

	ThreatHud.SteamIdentityResolver =
		SteamIdentityResolver;

})(ThreatHud);