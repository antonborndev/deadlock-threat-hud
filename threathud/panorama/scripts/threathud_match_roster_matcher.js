var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_PLAYERS =
		12;

	function normalizeName(value) {
		/*
		 * Intentionally do not call:
		 *
		 *     String.normalize('NFKC')
		 *
		 * In Panorama, this native Unicode call
		 * is considered a possible cause
		 * of client crashes.
		 */
		return String(
			value || ''
		)
			.replace(
				/[\u200B\u200C\u200D\uFEFF]/g,
				''
			)
			.replace(
				/\s+/g,
				' '
			)
			.replace(
				/^\s+|\s+$/g,
				''
			)
			.toLowerCase();
	}

	function MatchRosterMatcher(
		matchRoster,
		localHostClient,
		logger
	) {
		this._matchRoster =
			matchRoster;

		this._client =
			localHostClient;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._running =
			false;

		this._lastResult =
			null;
	}

	MatchRosterMatcher.prototype
		.matchCurrentRoster =
		function (callback) {
			if (this._running) {
				return false;
			}

			this._running =
				true;

			var rosterPlayers =
				this._readRoster();

			if (!rosterPlayers) {
				this._finish(
					callback,

					this._createError(
						'roster-not-ready',

						'MatchRoster does not yet contain ' +
							'12 ready players.',

						null
					),

					null
				);

				return false;
			}

			this._log(
				'MatchRosterMatcher: roster ready' +
					' | players=' +
					rosterPlayers.length
			);

			var self =
				this;

			this._client
				.getCurrentMatchPlayerIdentities(
					function (
						transportError,
						identityResult
					) {
						if (transportError) {
							self._finish(
								callback,
								transportError,
								null
							);

							return;
						}

						self._log(
							'MatchRosterMatcher: identities decoded' +
								' | players=' +
								(
									identityResult
										? identityResult.count
										: 0
								)
						);

						if (
							!identityResult ||
							identityResult.count !==
								EXPECTED_PLAYERS
						) {
							self._finish(
								callback,

								self._createError(
									'unexpected-identity-count',

									'Bridge returned an invalid ' +
										'number of identity entries.',

									identityResult
										? identityResult.count
										: null
								),

								null
							);

							return;
						}

						var result;

						try {
							self._log(
								'MatchRosterMatcher: matching START'
							);

							result =
								self._buildResult(
									rosterPlayers,
									identityResult.players
								);

							self._log(
								'MatchRosterMatcher: matching DONE' +
									' | resolved=' +
									result.resolvedCount +
									' | ambiguous=' +
									result.ambiguousCount +
									' | pending=' +
									result.pendingCount
							);
						} catch (matchingError) {
							self._finish(
								callback,

								self._createError(
									'matching-error',

									'Error matching roster ' +
										'and Steam identities.',

									String(
										matchingError
									)
								),

								null
							);

							return;
						}

						self._finish(
							callback,
							null,
							result
						);
					}
				);

			return true;
		};

	MatchRosterMatcher.prototype
		.getLastResult =
		function () {
			return this._lastResult;
		};

	MatchRosterMatcher.prototype
		._readRoster =
		function () {
			if (
				!this._matchRoster
					.connect()
			) {
				return null;
			}

			if (
				!this._matchRoster
					.readPlayers()
			) {
				return null;
			}

			var players =
				this._matchRoster
					.getPlayers();

			if (
				players.length !==
					EXPECTED_PLAYERS
			) {
				return null;
			}

			return players;
		};

	MatchRosterMatcher.prototype
		._buildResult =
		function (
			rosterPlayers,
			identityPlayers
		) {
			var matches =
				[];

			var usedAccountIds =
				[];

			var resolvedCount =
				0;

			var ambiguousCount =
				0;

			var pendingCount =
				0;

			for (
				var rosterIndex = 0;
				rosterIndex <
					rosterPlayers.length;
				rosterIndex++
			) {
				var rosterPlayer =
					rosterPlayers[
						rosterIndex
					];

				var normalizedPlayerName =
					normalizeName(
						rosterPlayer.playerName
					);

				var rosterNameCount =
					this._countRosterName(
						rosterPlayers,
						normalizedPlayerName
					);

				var identityCandidates =
					this._findIdentityCandidates(
						identityPlayers,
						normalizedPlayerName
					);

				var status =
					'pending';

				var identity =
					null;

				if (
					normalizedPlayerName === '' ||
					identityCandidates.length === 0
				) {
					status =
						'pending';

					pendingCount++;
				} else if (
					rosterNameCount !== 1 ||
					identityCandidates.length !== 1
				) {
					status =
						'ambiguous';

					ambiguousCount++;
				} else {
					status =
						'resolved';

					identity =
						identityCandidates[0];

					usedAccountIds.push(
						identity.accountIdText
					);

					resolvedCount++;
				}

				matches.push(
					this._createMatch(
						rosterPlayer,
						normalizedPlayerName,
						status,
						identity,
						identityCandidates
					)
				);
			}

			return {
				count:
					matches.length,

				resolvedCount:
					resolvedCount,

				ambiguousCount:
					ambiguousCount,

				pendingCount:
					pendingCount,

				allResolved:
					resolvedCount ===
						EXPECTED_PLAYERS,

				matches:
					matches,

				unmatchedIdentities:
					this._findUnmatchedIdentities(
						identityPlayers,
						usedAccountIds
					)
			};
		};

	MatchRosterMatcher.prototype
		._countRosterName =
		function (
			rosterPlayers,
			normalizedName
		) {
			var count =
				0;

			for (
				var index = 0;
				index <
					rosterPlayers.length;
				index++
			) {
				if (
					normalizeName(
						rosterPlayers[index]
							.playerName
					) === normalizedName
				) {
					count++;
				}
			}

			return count;
		};

	MatchRosterMatcher.prototype
		._findIdentityCandidates =
		function (
			identityPlayers,
			normalizedName
		) {
			var result =
				[];

			for (
				var index = 0;
				index <
					identityPlayers.length;
				index++
			) {
				var identity =
					identityPlayers[index];

				if (
					normalizeName(
						identity.personaName
					) === normalizedName
				) {
					result.push(
						identity
					);
				}
			}

			return result;
		};

	MatchRosterMatcher.prototype
		._createMatch =
		function (
			rosterPlayer,
			normalizedPlayerName,
			status,
			identity,
			identityCandidates
		) {
			var candidates =
				[];

			for (
				var index = 0;
				index <
					identityCandidates.length;
				index++
			) {
				var candidate =
					identityCandidates[index];

				candidates.push({
					accountId:
						candidate.accountId,

					accountIdText:
						candidate.accountIdText,

					personaName:
						candidate.personaName,

					isLocal:
						candidate.isLocal
				});
			}

			return {
				status:
					status,

				rosterIndex:
					rosterPlayer.rosterIndex,

				team:
					rosterPlayer.team,

				teamIndex:
					rosterPlayer.teamIndex,

				playerName:
					rosterPlayer.playerName,

				normalizedPlayerName:
					normalizedPlayerName,

				heroName:
					rosterPlayer.heroName,

				panel:
					rosterPlayer.panel,

				playerNameLabel:
					rosterPlayer.playerNameLabel,

				heroNameLabel:
					rosterPlayer.heroNameLabel,

				accountId:
					identity
						? identity.accountId
						: null,

				accountIdText:
					identity
						? identity.accountIdText
						: '',

				personaName:
					identity
						? identity.personaName
						: '',

				isLocal:
					identity
						? identity.isLocal
						: false,

				candidates:
					candidates
			};
		};

	MatchRosterMatcher.prototype
		._findUnmatchedIdentities =
		function (
			identityPlayers,
			usedAccountIds
		) {
			var result =
				[];

			for (
				var index = 0;
				index <
					identityPlayers.length;
				index++
			) {
				var identity =
					identityPlayers[index];

				if (
					!this._containsText(
						usedAccountIds,
						identity.accountIdText
					)
				) {
					result.push({
						accountId:
							identity.accountId,

						accountIdText:
							identity.accountIdText,

						personaName:
							identity.personaName,

						isLocal:
							identity.isLocal
					});
				}
			}

			return result;
		};

	MatchRosterMatcher.prototype
		._containsText =
		function (
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
		};

	MatchRosterMatcher.prototype
		._finish =
		function (
			callback,
			error,
			result
		) {
			this._running =
				false;

			if (!error) {
				this._lastResult =
					result;
			}

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

	MatchRosterMatcher.prototype
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

	ThreatHud.MatchRosterMatcher =
		MatchRosterMatcher;

})(ThreatHud);