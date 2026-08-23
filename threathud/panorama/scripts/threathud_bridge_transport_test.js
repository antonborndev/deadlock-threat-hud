var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function BridgeTransportTest(
		matchRosterMatcher,
		playerStatsClient,
		statsOverlay,
		logger
	) {
		this._matcher =
			matchRosterMatcher;

		this._playerStatsClient =
			playerStatsClient;

		this._statsOverlay =
			statsOverlay;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._running =
			false;
	}

	BridgeTransportTest.prototype.run =
		function () {
			if (this._running) {
				this._log(
					'Current match stats: request is already in progress'
				);

				return false;
			}

			this._running =
				true;

			this._log(
				'Current match stats: START'
			);

			var self =
				this;

			var matchStarted =
				this._matcher
					.matchCurrentRoster(
						function (
							matchError,
							matchResult
						) {
							if (matchError) {
								self._finishWithError(
									'match',
									matchError
								);

								return;
							}

							self._log(
								'Current match stats: MATCHED' +
									' | resolved=' +
									matchResult.resolvedCount +
									' | ambiguous=' +
									matchResult.ambiguousCount +
									' | pending=' +
									matchResult.pendingCount
							);

							if (
								!matchResult.allResolved
							) {
								/*
								 * Sandbox:
								 *
								 * the only real Steam player is
								 * the local user;
								 *
								 * the other 11 slots are bots
								 * and therefore cannot be matched
								 * to Steam Recent identities.
								 */
								if (
									self._isSandboxRoster(
										matchResult
									)
								) {
									self._renderSandboxPreview(
										matchResult
									);

									return;
								}

								self._running =
									false;

								self._log(
									'Current match stats: STOP' +
										' | reason=not all players are matched'
								);

								return;
							}

							self._requestRealStats(
								matchResult
							);
						}
					);

			if (!matchStarted) {
				this._running =
					false;
			}

			return matchStarted;
		};

	BridgeTransportTest.prototype
		._requestRealStats =
		function (matchResult) {
			var self =
				this;

			var statsStarted =
				this._playerStatsClient
					.getForMatches(
						matchResult.matches,

						function (
							statsError,
							statsResult
						) {
							self._running =
								false;

							if (statsError) {
								self._log(
									'Current match stats ERROR' +
										' | stage=stats' +
										' | code=' +
										statsError.code +
										' | message=' +
										statsError.message +
										(
											statsError.detail !==
												null
												? ' | detail=' +
													String(
														statsError.detail
													)
												: ''
										)
								);

								return;
							}

							self._logStats(
								statsResult
							);

							var rendered =
								self._statsOverlay
									.render(
										statsResult.players
									);

							self._log(
								'Current match stats overlay' +
									' | rendered=' +
									rendered +
									' | mode=real'
							);

							self._log(
								'Current match stats: DONE'
							);
						}
					);

			if (!statsStarted) {
				this._running =
					false;
			}
		};

	BridgeTransportTest.prototype
		._isSandboxRoster =
		function (matchResult) {
			if (
				matchResult.resolvedCount !== 1 ||
				matchResult.pendingCount !== 11 ||
				matchResult.ambiguousCount !== 0
			) {
				return false;
			}

			var resolvedLocalCount =
				0;

			for (
				var index = 0;
				index <
					matchResult.matches.length;
				index++
			) {
				var match =
					matchResult.matches[index];

				if (
					match.status === 'resolved' &&
					match.isLocal
				) {
					resolvedLocalCount++;
				}
			}

			return (
				resolvedLocalCount === 1
			);
		};

	BridgeTransportTest.prototype
		._renderSandboxPreview =
		function (matchResult) {
			var previewPlayers =
				this._buildSandboxPreviewPlayers(
					matchResult.matches
				);

			this._log(
				'Current match stats: SANDBOX UI PREVIEW' +
					' | players=' +
					previewPlayers.length
			);

			this._logStats({
				count:
					previewPlayers.length,

				okCount:
					previewPlayers.length,

				statsNotFoundCount:
					0,

				heroUnknownCount:
					0,

				heroAmbiguousCount:
					0,

				players:
					previewPlayers
			});

			var rendered =
				this._statsOverlay.render(
					previewPlayers
				);

			this._running =
				false;

			this._log(
				'Current match stats overlay' +
					' | rendered=' +
					rendered +
					' | mode=sandbox-preview'
			);

			this._log(
				'Current match stats: DONE'
			);
		};

	BridgeTransportTest.prototype
		._buildSandboxPreviewPlayers =
		function (matches) {
			var result =
				[];

			for (
				var index = 0;
				index < matches.length;
				index++
			) {
				var match =
					matches[index];

				/*
				 * Stable test values:
				 * they depend only on rosterIndex,
				 * so they do not change between RUNs.
				 */
				var matchesPlayed =
					12 +
					index *
						17;

				var desiredWinRate =
					38 +
					(
						index *
						7
					) %
						27;

				var wins =
					Math.round(
						matchesPlayed *
							desiredWinRate /
							100
					);

				var actualWinRate =
					matchesPlayed > 0
						? wins *
							100.0 /
							matchesPlayed
						: 0;

				result.push({
					index:
						index,

					rosterIndex:
						match.rosterIndex,

					team:
						match.team,

					teamIndex:
						match.teamIndex,

					playerName:
						match.playerName,

					personaName:
						match.status ===
							'resolved'
								? match.personaName
								: match.playerName,

					heroName:
						match.heroName,

					accountId:
						match.status ===
							'resolved'
								? match.accountId
								: 0,

					accountIdText:
						match.status ===
							'resolved'
								? match.accountIdText
								: '',

					isLocal:
						match.isLocal,

					statusCode:
						0,

					status:
						'ok',

					heroId:
						0,

					matchesPlayed:
						matchesPlayed,

					wins:
						wins,

					winRatePercent:
						actualWinRate,

					isSandboxPreview:
						true
				});
			}

			return result;
		};

	BridgeTransportTest.prototype
		._logStats =
		function (statsResult) {
			this._log(
				'Current match stats RESULT' +
					' | players=' +
					statsResult.count +
					' | ok=' +
					statsResult.okCount +
					' | statsNotFound=' +
					statsResult.statsNotFoundCount +
					' | heroUnknown=' +
					statsResult.heroUnknownCount +
					' | heroAmbiguous=' +
					statsResult.heroAmbiguousCount
			);

			for (
				var index = 0;
				index <
					statsResult.players.length;
				index++
			) {
				var player =
					statsResult.players[index];

				this._log(
					'Stats [' +
						player.rosterIndex +
						']' +
						' | team=' +
						player.team +
						' | player=' +
						player.playerName +
						' | hero=' +
						player.heroName +
						' | accountID=' +
						player.accountIdText +
						' | status=' +
						player.status +
						' | apiHeroID=' +
						player.heroId +
						' | matches=' +
						player.matchesPlayed +
						' | wins=' +
						player.wins +
						' | winrate=' +
						player.winRatePercent
							.toFixed(
								2
							) +
						'%' +
						' | local=' +
						player.isLocal +
						(
							player.isSandboxPreview
								? ' | preview=true'
								: ''
						)
				);
			}
		};

	BridgeTransportTest.prototype
		._finishWithError =
		function (
			stage,
			error
		) {
			this._running =
				false;

			this._log(
				'Current match stats ERROR' +
					' | stage=' +
					stage +
					' | code=' +
					error.code +
					' | message=' +
					error.message +
					(
						error.detail !== null
							? ' | detail=' +
								String(
									error.detail
								)
							: ''
					)
			);
		};

	ThreatHud.BridgeTransportTest =
		BridgeTransportTest;

})(ThreatHud);