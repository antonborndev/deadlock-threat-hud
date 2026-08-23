var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var MESSAGE_PLAYER_STATS =
		2;

	var BYTES_PER_PLAYER =
		14;

	function readUInt32LittleEndian(
		bytes,
		offset
	) {
		return (
			bytes[offset] +
			bytes[offset + 1] *
				256 +
			bytes[offset + 2] *
				65536 +
			bytes[offset + 3] *
				16777216
		);
	}

	function decodeReaction(
		value
	) {
		if (value === 0) {
			return 0;
		}

		if (value === 1) {
			return 1;
		}

		if (value === 255) {
			return -1;
		}

		throw new Error(
			'Invalid player reaction: ' +
				value
		);
	}

	function getStatusName(
		statusCode
	) {
		switch (statusCode) {
			case 0:
				return 'ok';

			case 1:
				return 'hero-unknown';

			case 2:
				return 'hero-ambiguous';

			case 3:
				return 'stats-not-found';

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
				'Player-stats payload is empty.'
			);
		}

		var playerCount =
			payload[0];

		if (
			playerCount !==
				requestedPlayers.length
		) {
			throw new Error(
				'Player-stats entry count does not match the compact request' +
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
				'Invalid player-stats payload size' +
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

		var heroUnknownCount =
			0;

		var heroAmbiguousCount =
			0;

		var statsNotFoundCount =
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

			var heroId =
				readUInt32LittleEndian(
					payload,
					offset + 1
				);

			var matchesPlayed =
				readUInt32LittleEndian(
					payload,
					offset + 5
				);

			var wins =
				readUInt32LittleEndian(
					payload,
					offset + 9
				);

			var reaction =
				decodeReaction(
					payload[
						offset + 13
					]
				);

			var status =
				getStatusName(
					statusCode
				);

			if (statusCode === 0) {
				okCount++;
			} else if (
				statusCode === 1
			) {
				heroUnknownCount++;
			} else if (
				statusCode === 2
			) {
				heroAmbiguousCount++;
			} else if (
				statusCode === 3
			) {
				statsNotFoundCount++;
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

			player.heroId =
				heroId;

			player.matchesPlayed =
				matchesPlayed;

			player.wins =
				wins;

			player.reaction =
				reaction;

			player.winRatePercent =
				matchesPlayed > 0
					? (
						wins *
						100.0 /
						matchesPlayed
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

			heroUnknownCount:
				heroUnknownCount,

			heroAmbiguousCount:
				heroAmbiguousCount,

			statsNotFoundCount:
				statsNotFoundCount,

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

		player.heroId =
			0;

		player.matchesPlayed =
			0;

		player.wins =
			0;

		player.reaction =
			0;

		player.winRatePercent =
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

			statsNotFoundCount:
				compactResult.statsNotFoundCount,

			heroUnknownCount:
				compactResult.heroUnknownCount,

			heroAmbiguousCount:
				compactResult.heroAmbiguousCount,

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
			' | statsNotFound=' +
				result.statsNotFoundCount +
			' | heroUnknown=' +
				result.heroUnknownCount +
			' | heroAmbiguous=' +
				result.heroAmbiguousCount
		);
	}

	ThreatHud.PlayerStatsClient =
		ThreatHud
			.ResolvedRosterClientUtils
			.createClientConstructor({
				clientName:
					'PlayerStatsClient',

				channel:
					'player-stats',

				messageType:
					MESSAGE_PLAYER_STATS,

				includeHeroName:
					true,

				invalidPayloadCode:
					'invalid-player-stats-payload',

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