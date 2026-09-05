var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	/*
	 * ==========================================
	 * DEBUG CONFIGURATION
	 * ==========================================
	 */

	var DEBUG_ENABLED =
	 false;

	var MESSAGE_SERVICE_STATUS_ACK = 8;

	function noopLog() {
	}

	function setDebugPanelVisible(
		context,
		visible
	) {
		if (
			!context ||
			!context.IsValid()
		) {
			return;
		}

		var panel =
			context.FindChildTraverse(
				'ThreatHudLogWindow'
			);

		if (
			!panel ||
			!panel.IsValid()
		) {
			return;
		}

		panel.visible =
			visible;

		panel.enabled =
			visible;

		panel.hittest =
			visible;
	}

	var context =
		$.GetContextPanel();

	var topPanel =
		ThreatHud.PanelUtils
			.getTopPanel(
				context
			);

	setDebugPanelVisible(
		context,
		DEBUG_ENABLED
	);

	var logger =
		null;

	var hudMsg =
		noopLog;

	if (DEBUG_ENABLED) {
		logger =
			new ThreatHud.HudLogger(
				context
			);

		hudMsg =
			logger.getWriter();
	}

	var localHostClient =
		new ThreatHud.LocalHostClient(
			context,
			'http://127.0.0.1:28741',
			hudMsg
		);

	var serviceStatusClient =
		new ThreatHud.ServiceStatusClient(
			localHostClient,
			hudMsg
		);

	var playerStatsClient =
		new ThreatHud.PlayerStatsClient(
			localHostClient,
			hudMsg
		);

	var playerRanksClient =
		new ThreatHud.PlayerRanksClient(
			localHostClient,
			hudMsg
		);

	var playerReactionClient =
		new ThreatHud.PlayerReactionClient(
			localHostClient,
			hudMsg
		);

	var heroDamageClient =
		new ThreatHud.HeroDamageClient(
			localHostClient,
			hudMsg
		);

	var purchaseHistory =
		new ThreatHud.PurchaseHistory();

	var itemCatalog =
		new ThreatHud.ItemCatalog(
			topPanel,
			hudMsg
		);

	var enemyTeam =
		new ThreatHud.EnemyTeam(
			topPanel,
			hudMsg
		);

	var matchRoster =
		new ThreatHud.MatchRoster(
			topPanel,
			hudMsg
		);

	var recentPurchaseObserver =
		new ThreatHud
			.RecentPurchaseObserver(
				topPanel,
				purchaseHistory,
				hudMsg
			);

	var enemyTeamMonitor =
		new ThreatHud
			.EnemyTeamMonitor(
				enemyTeam,
				hudMsg
			);

	var matchRosterMonitor =
		new ThreatHud
			.MatchRosterMonitor(
				matchRoster,
				hudMsg
			);

	var matchRosterMatcher =
		new ThreatHud
			.MatchRosterMatcher(
				matchRoster,
				localHostClient,
				hudMsg
			);

	var currentMatchLaneAdvisorOverlay =
		new ThreatHud
			.CurrentMatchLaneAdvisorOverlay(
				hudMsg
			);

	/*
	 * Advisor uses the same LocalHostClient
	 * as the other Bridge channels.
	 *
	 * The result handler is responsible only for the UI.
	 * The stats/ranks workflow does not depend on it.
	 */
	var laneAdvisorClient =
		new ThreatHud.LaneAdvisorClient(
			localHostClient,
			hudMsg,

			function (
				error,
				result,
				matches
			) {
				if (
					error ||
					!result
				) {
					currentMatchLaneAdvisorOverlay
						.clear();

					return;
				}

				currentMatchLaneAdvisorOverlay
					.render(
						matches,
						result
					);
			}
		);

	var currentMatchStatsOverlay =
		new ThreatHud
			.CurrentMatchStatsOverlay(
				topPanel,
				hudMsg
			);

	var currentMatchStatsDisplayGate =
		new ThreatHud.ModuleDisplayGate(
			currentMatchStatsOverlay,
			'Winrate',
			hudMsg
		);

	var currentMatchRankOverlay =
		new ThreatHud
			.CurrentMatchRankOverlay(
				topPanel,
				hudMsg
			);

	var currentMatchRankDisplayGate =
		new ThreatHud.ModuleDisplayGate(
			currentMatchRankOverlay,
			'Rank',
			hudMsg
		);

	var currentMatchReactionOverlay =
		new ThreatHud
			.CurrentMatchReactionOverlay(
				topPanel,
				hudMsg
			);

	var currentMatchHeroDamageOverlay =
		new ThreatHud
			.CurrentMatchHeroDamageOverlay(
				hudMsg
			);

	var currentMatchReactionMonitor =
		new ThreatHud
			.CurrentMatchReactionMonitor(
				playerReactionClient,
				currentMatchReactionOverlay,
				currentMatchStatsDisplayGate,
				hudMsg
			);

	var currentMatchStatsOverlayAdapter =
		new ThreatHud
			.CurrentMatchReactionStatsOverlayAdapter(
				currentMatchStatsDisplayGate,
				currentMatchReactionMonitor
			);

	var currentMatchRankOverlayAdapter =
		new ThreatHud
			.CurrentMatchReactionRankOverlayAdapter(
				currentMatchRankDisplayGate,
				currentMatchReactionMonitor
			);

	var currentMatchRankMonitor =
		new ThreatHud
				.CurrentMatchRankMonitor(
					playerRanksClient,
					currentMatchRankOverlayAdapter,
					hudMsg,
					serviceStatusClient
				);

	var currentMatchHeroDamageMonitor =
		new ThreatHud
			.CurrentMatchHeroDamageMonitor(
				heroDamageClient,
				currentMatchHeroDamageOverlay,
				matchRosterMatcher,
				hudMsg
			);

	var currentMatchStatsMonitor =
		new ThreatHud
			.CurrentMatchStatsMonitor(
				matchRosterMatcher,
				playerStatsClient,
				currentMatchStatsOverlayAdapter,
				hudMsg,
					currentMatchRankMonitor
						.getRosterContextChangedHandler(),
					laneAdvisorClient,
					serviceStatusClient
				);

	var matchScreenMonitor =
		new ThreatHud.MatchScreenMonitor(
			matchRoster,
			currentMatchStatsMonitor,
			currentMatchHeroDamageMonitor,
			hudMsg,
			null,

			function (
				won,
				observedAtUnixMs,
				callback
			) {
				return localHostClient.requestPacket(
					'current-match-result',

					{
						won:
							won
								? '1'
								: '0',

						observedAtUnixMs:
							String(
								observedAtUnixMs
							)
					},

					function (
						error,
						packet
					) {
						if (typeof callback !== 'function') {
							return;
						}

						if (error) {
							callback(error, null);

							return;
						}

						if (
							!packet ||
							packet.messageType !==
								MESSAGE_SERVICE_STATUS_ACK ||
							!packet.payload ||
							packet.payload.length !== 1 ||
							packet.payload[0] !== 1
						) {
							callback(
								{
									code:
										'invalid-match-result-ack',

									message:
										'Bridge did not confirm match result.'
								},

								null
							);

							return;
						}

						callback(null, packet);
					}
				);
			}
		);

	var moduleSettingsMonitor =
		new ThreatHud.ModuleSettingsMonitor(
			localHostClient,
			hudMsg,

			function (settings) {
				currentMatchStatsDisplayGate
					.setEnabled(
						settings.winrate
					);

				currentMatchRankDisplayGate
					.setEnabled(
						settings.rank
					);

				laneAdvisorClient
					.setEnabled(
						settings.adviser
					);

				currentMatchHeroDamageMonitor
					.setEnabled(
						settings.heroDamage
					);
			}
		);

	var ManualTestbtn =
		new ThreatHud
			.ManualTest(
				{logger:hudMsg,
					topPanel:topPanel
				}
			);


	if (DEBUG_ENABLED) {
		var debugControls =
			new ThreatHud.DebugControls({
				context:
					context,

				manualTest:
					ManualTestbtn,

				logger:
					logger,

				log:
					hudMsg
			});

		debugControls.bind();
	}

	//matchRosterMonitor.start();

	//enemyTeamMonitor.start();

	//recentPurchaseObserver.start();

	moduleSettingsMonitor.start();

	matchScreenMonitor.start();

})(ThreatHud);
