var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_ROSTER_PLAYERS =
		12;

	function createError(
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
	}

	function invokeCallback(
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
	}

	function containsText(
		values,
		expected
	) {
		for (
			var index = 0;
			index < values.length;
			index++
		) {
			if (
				values[index] ===
					expected
			) {
				return true;
			}
		}

		return false;
	}

	/*
	 * Creates one immutable snapshot
	 * of identity and Panorama bindings.
	 *
	 * It is used simultaneously:
	 *
	 * - as the compact request binding;
	 * - as the basis for an unresolved placeholder;
	 * - when merging back into the full roster.
	 */
	function createIdentitySnapshot(
		match
	) {
		var resolved =
			match.status ===
				'resolved';

		return {
			index:
				match.rosterIndex,

			rosterIndex:
				match.rosterIndex,

			team:
				match.team,

			teamIndex:
				match.teamIndex,

			playerName:
				match.playerName,

			personaName:
				resolved
					? match.personaName
					: match.playerName,

			heroName:
				match.heroName,

			panel:
				match.panel,

			playerNameLabel:
				match.playerNameLabel,

			heroNameLabel:
				match.heroNameLabel,

			accountId:
				resolved
					? match.accountId
					: 0,

			accountIdText:
				resolved
					? match.accountIdText
					: '',

			isLocal:
				match.isLocal
		};
	}

	/*
	 * The only place for reverse
	 * matching of the compact result.
	 *
	 * rosterIndex is intentionally omitted.
	 *
	 * The following must match:
	 *
	 * - native player panel;
	 * - saved PlayerName/HeroName labels;
	 * - accountId;
	 * - playerName;
	 * - heroName.
	 */
	function sameResolvedIdentity(
		resultPlayer,
		match
	) {
		return !!(
			resultPlayer &&
			match &&
			match.status ===
				'resolved' &&
			resultPlayer.panel ===
				match.panel &&
			resultPlayer.playerNameLabel ===
				match.playerNameLabel &&
			resultPlayer.heroNameLabel ===
				match.heroNameLabel &&
			resultPlayer.accountIdText ===
				match.accountIdText &&
			resultPlayer.playerName ===
				match.playerName &&
			resultPlayer.heroName ===
				match.heroName
		);
	}

	function findResultPlayer(
		compactPlayers,
		match
	) {
		var found =
			null;

		for (
			var index = 0;
			index < compactPlayers.length;
			index++
		) {
			var player =
				compactPlayers[index];

			if (
				!sameResolvedIdentity(
					player,
					match
				)
			) {
				continue;
			}

			/*
			 * Two compact entries must not
			 * correspond to the same roster identity.
			 */
			if (found) {
				return null;
			}

			found =
				player;
		}

		return found;
	}

	/*
	 * Receives the full 12-slot roster,
	 * but builds a compact transport request
	 * only for resolved entries.
	 *
	 * includeHeroName:
	 *
	 * true  → player-stats: aN + hN
	 * false → player-ranks: aN
	 */
	function prepareRequest(
		matches,
		includeHeroName
	) {
		if (
			!matches ||
			matches.length !==
				EXPECTED_ROSTER_PLAYERS
		) {
			return {
				error:
					createError(
						'invalid-match-count',

						'A full roster of ' +
							EXPECTED_ROSTER_PLAYERS +
							' entries is required.',

						matches
							? matches.length
							: null
					)
			};
		}

		var parameters =
			{};

		var requestedPlayers =
			[];

		var accountIds =
			[];

		for (
			var rosterPosition = 0;
			rosterPosition < matches.length;
			rosterPosition++
		) {
			var match =
				matches[rosterPosition];

			if (!match) {
				return {
					error:
						createError(
							'invalid-roster-entry',
							'Roster contains an empty entry.',
							rosterPosition
						)
				};
			}

			if (
				match.status !==
					'resolved'
			) {
				continue;
			}

			if (!match.accountIdText) {
				return {
					error:
						createError(
							'incomplete-roster-entry',
							'accountID is missing from a resolved roster entry.',
							rosterPosition
						)
				};
			}

			if (
				includeHeroName &&
				!match.heroName
			) {
				return {
					error:
						createError(
							'incomplete-roster-entry',
							'heroName is missing from a resolved roster entry.',
							rosterPosition
						)
				};
			}

			if (
				containsText(
					accountIds,
					match.accountIdText
				)
			) {
				return {
					error:
						createError(
							'duplicate-account-id',
							'Duplicate accountID detected.',
							match.accountIdText
						)
				};
			}

			accountIds.push(
				match.accountIdText
			);

			var requestIndex =
				requestedPlayers.length;

			parameters[
				'a' + requestIndex
			] =
				match.accountIdText;

			if (includeHeroName) {
				parameters[
					'h' + requestIndex
				] =
					match.heroName;
			}

			requestedPlayers.push(
				createIdentitySnapshot(
					match
				)
			);
		}

		if (
			requestedPlayers.length ===
				0
		) {
			return {
				error:
					createError(
						'no-resolved-players',
						'Roster has no resolved players.',
						null
					)
			};
		}

		parameters.count =
			requestedPlayers.length;

		return {
			error:
				null,

			parameters:
				parameters,

			requestedPlayers:
				requestedPlayers
		};
	}

	/*
	 * Returns the compact result into the full
	 * 12-slot roster.
	 *
	 * The result is not inserted by compact index
	 * and is not inserted by rosterIndex.
	 *
	 * For each resolved roster entry,
	 * an exact sameResolvedIdentity() check is performed.
	 */
	function mergeResult(
		matches,
		compactPlayers,
		configureUnresolved
	) {
		if (
			!matches ||
			matches.length !==
				EXPECTED_ROSTER_PLAYERS ||
			!compactPlayers ||
			typeof configureUnresolved !==
				'function'
		) {
			return null;
		}

		var players =
			[];

		var matchedResolvedCount =
			0;

		var identityUnresolvedCount =
			0;

		for (
			var index = 0;
			index < matches.length;
			index++
		) {
			var match =
				matches[index];

			if (!match) {
				return null;
			}

			if (
				match.status !==
					'resolved'
			) {
				var unresolvedPlayer =
					createIdentitySnapshot(
						match
					);

				configureUnresolved(
					unresolvedPlayer,
					match
				);

				players.push(
					unresolvedPlayer
				);

				identityUnresolvedCount++;

				continue;
			}

			var resultPlayer =
				findResultPlayer(
					compactPlayers,
					match
				);

			if (!resultPlayer) {
				return null;
			}

			players.push(
				resultPlayer
			);

			matchedResolvedCount++;
		}

		if (
			matchedResolvedCount !==
				compactPlayers.length
		) {
			return null;
		}

		return {
			players:
				players,

			identityUnresolvedCount:
				identityUnresolvedCount
		};
	}

	/*
	 * Shared runtime for two clients:
	 *
	 * - PlayerStatsClient;
	 * - PlayerRanksClient.
	 *
	 * The following are kept in one place:
	 *
	 * - running gate;
	 * - compact request;
	 * - requestPacket();
	 * - messageType validation;
	 * - compact decode callback;
	 * - reverse merge;
	 * - shared callback/error lifecycle.
	 */
	function initializeClient(
		client,
		localHostClient,
		logger,
		configuration
	) {
		client._transport =
			localHostClient;

		client._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		client._configuration =
			configuration;

		client._running =
			false;
	}

	function getForMatches(
		matches,
		callback
	) {
		var configuration =
			this._configuration;

		if (this._running) {
			invokeCallback(
				callback,

				createError(
					'already-running',

					configuration.clientName +
						' request is already in progress.',

					null
				),

				null
			);

			return false;
		}

		var request =
			prepareRequest(
				matches,
				configuration.includeHeroName
			);

		if (request.error) {
			invokeCallback(
				callback,
				request.error,
				null
			);

			return false;
		}

		this._running =
			true;

		this._log(
			configuration.clientName +
				': REQUEST' +
				' | roster=' +
				matches.length +
				' | resolved=' +
				request.requestedPlayers.length
		);

		var self =
			this;

		return this._transport.requestPacket(
			configuration.channel,
			request.parameters,

			function (
				error,
				packet
			) {
				self._running =
					false;

				if (error) {
					invokeCallback(
						callback,
						error,
						null
					);

					return;
				}

				if (
					!packet ||
					packet.messageType !==
						configuration.messageType
				) {
					invokeCallback(
						callback,

						createError(
							'unexpected-message-type',

							'Bridge returned an unexpected type ' +
								configuration.channel +
								' message.',

							packet
								? packet.messageType
								: null
						),

						null
					);

					return;
				}

				try {
					var compactResult =
						configuration.decodePayload(
							packet.payload,
							request.requestedPlayers
						);

					var merged =
						mergeResult(
							matches,
							compactResult.players,
							configuration.configureUnresolved
						);

					if (!merged) {
						throw new Error(
							'Failed to match compact ' +
								configuration.channel +
								' with the source roster by identity.'
						);
					}

					var result =
						configuration.buildResult(
							compactResult,
							merged
						);

					self._log(
						configuration.clientName +
							': RESPONSE' +
							' | roster=' +
							result.count +
							' | resolved=' +
							compactResult.count +
							' | unresolved=' +
							result.identityUnresolvedCount +
							configuration.formatResponseSummary(
								result
							)
					);

					invokeCallback(
						callback,
						null,
						result
					);
				} catch (
					decodeError
				) {
					invokeCallback(
						callback,

						createError(
							configuration.invalidPayloadCode,

							'Failed to parse or match ' +
								configuration.channel +
								' payload.',

							String(
								decodeError
							)
						),

						null
					);
				}
			}
		);
	}

	function createClientConstructor(
		configuration
	) {
		function ConfiguredResolvedRosterClient(
			localHostClient,
			logger
		) {
			initializeClient(
				this,
				localHostClient,
				logger,
				configuration
			);
		}

		ConfiguredResolvedRosterClient.prototype
			.getForMatches =
			getForMatches;

		return ConfiguredResolvedRosterClient;
	}

	ThreatHud.ResolvedRosterClientUtils = {
		createClientConstructor:
			createClientConstructor
	};

})(ThreatHud);