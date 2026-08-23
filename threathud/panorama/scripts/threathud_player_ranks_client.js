var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var MESSAGE_PLAYER_RANKS =
		4;

	/*
	 * One compact result:
	 *
	 * byte status
	 * byte rank
	 * byte subrank
	 */
	var BYTES_PER_PLAYER =
		3;

	function getStatusName(
		statusCode
	) {
		switch (statusCode) {
			case 0:
				return 'ok';

			case 1:
				return 'unranked';

			case 2:
				return 'protected';

			case 3:
				return 'not-found';

			case 4:
				return 'api-error';

			default:
				return (
					'unknown-status-' +
						statusCode
				);
		}
	}

	function decodePayload(
		payload,
		requestedPlayers
	) {
		if (
			!payload ||
			payload.length < 1
		) {
			throw new Error(
				'Player-ranks payload is empty.'
			);
		}

		var playerCount =
			payload[0];

		if (
			playerCount !==
				requestedPlayers.length
		) {
			throw new Error(
				'Player-ranks entry count does not match the compact request' +
					' | requested=' +
					requestedPlayers.length +
					' | received=' +
					playerCount
			);
		}

		var expectedLength =
			1 +
			playerCount *
				BYTES_PER_PLAYER;

		if (
			payload.length !==
				expectedLength
		) {
			throw new Error(
				'Invalid player-ranks payload size' +
					' | expected=' +
					expectedLength +
					' | actual=' +
					payload.length
			);
		}

		var players =
			[];

		var okCount =
			0;

		var unrankedCount =
			0;

		var protectedCount =
			0;

		var notFoundCount =
			0;

		var apiErrorCount =
			0;

		for (
			var index = 0;
			index < playerCount;
			index++
		) {
			var offset =
				1 +
				index *
					BYTES_PER_PLAYER;

			var statusCode =
				payload[offset];

			var rank =
				payload[offset + 1];

			var subrank =
				payload[offset + 2];

			var status =
				getStatusName(
					statusCode
				);

			if (statusCode === 0) {
				if (
					rank < 1 ||
					rank > 11
				) {
					throw new Error(
						'Invalid rank for status=ok' +
							' | index=' +
							index +
							' | rank=' +
							rank
					);
				}

				if (
					subrank < 1 ||
					subrank > 6
				) {
					throw new Error(
						'Invalid subrank for status=ok' +
							' | index=' +
							index +
							' | subrank=' +
							subrank
					);
				}

				okCount++;
			} else {
				if (
					rank !== 0 ||
					subrank !== 0
				) {
					throw new Error(
						'Non-zero rank for an error status' +
							' | index=' +
							index +
							' | status=' +
							status +
							' | rank=' +
							rank +
							' | subrank=' +
							subrank
					);
				}

				if (statusCode === 1) {
					unrankedCount++;
				} else if (
					statusCode === 2
				) {
					protectedCount++;
				} else if (
					statusCode === 3
				) {
					notFoundCount++;
				} else if (
					statusCode === 4
				) {
					apiErrorCount++;
				}
			}

			/*
			 * Compact response index refers only
			 * to the compact request, not the HUD position.
			 */
			var player =
				requestedPlayers[index];

			player.statusCode =
				statusCode;

			player.status =
				status;

			player.rank =
				rank;

			player.subrank =
				subrank;

			player.badge =
				statusCode === 0
					? (
						rank *
						10 +
						subrank
					)
					: 0;

			player.isSandboxPreview =
				false;

			players.push(
				player
			);
		}

		return {
			count:
				players.length,

			okCount:
				okCount,

			unrankedCount:
				unrankedCount,

			protectedCount:
				protectedCount,

			notFoundCount:
				notFoundCount,

			apiErrorCount:
				apiErrorCount,

			players:
				players
		};
	}

	function configureIdentityUnresolved(
		player
	) {
		player.statusCode =
			255;

		player.status =
			'identity-unresolved';

		player.rank =
			0;

		player.subrank =
			0;

		player.badge =
			0;

		player.isSandboxPreview =
			false;
	}

	function buildResult(
		compactResult,
		merged
	) {
		return {
			count:
				merged.players.length,

			okCount:
				compactResult.okCount,

			unrankedCount:
				compactResult.unrankedCount,

			protectedCount:
				compactResult.protectedCount,

			notFoundCount:
				compactResult.notFoundCount,

			apiErrorCount:
				compactResult.apiErrorCount,

			identityUnresolvedCount:
				merged.identityUnresolvedCount,

			players:
				merged.players
		};
	}

	function formatResponseSummary(
		result
	) {
		return (
			' | ok=' +
				result.okCount +
			' | unranked=' +
				result.unrankedCount +
			' | protected=' +
				result.protectedCount +
			' | notFound=' +
				result.notFoundCount +
			' | apiError=' +
				result.apiErrorCount
		);
	}

	ThreatHud.PlayerRanksClient =
		ThreatHud
			.ResolvedRosterClientUtils
			.createClientConstructor({
				clientName:
					'PlayerRanksClient',

				channel:
					'player-ranks',

				messageType:
					MESSAGE_PLAYER_RANKS,

				includeHeroName:
					false,

				invalidPayloadCode:
					'invalid-player-ranks-payload',

				decodePayload:
					decodePayload,

				configureUnresolved:
					configureIdentityUnresolved,

				buildResult:
					buildResult,

				formatResponseSummary:
					formatResponseSummary
			});

})(ThreatHud);