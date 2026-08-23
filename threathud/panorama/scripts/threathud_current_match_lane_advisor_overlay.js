var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_PLAYERS =
		12;

	var ALLY_PLAYERS =
		6;

	var OVERLAY_ID =
		'ThreatHudLaneAdvisorStats';

	var WR_LABEL_ID =
		'ThreatHudLaneAdvisorWinRate';

	var SOULS_LABEL_ID =
		'ThreatHudLaneAdvisorSouls15';

	var OVERLAY_TOP =
		125;

	var OVERLAY_WIDTH =
		84;

	var OVERLAY_HEIGHT =
		35;

	var OVERLAY_BACKGROUND =
		'#080808d8';

	var OVERLAY_BORDER =
		'1px solid #ffffff24';

	/*
	 * Highlight BEST using the entire block.
	 *
	 * The color is set inline, like the other
	 * working Threat HUD styles.
	 */
	var BEST_BACKGROUND =
		'#62349be8';

	var BEST_BORDER =
		'1px solid #c89cff';

	var VISIBILITY_CHECK_INTERVAL =
		0.25;

	var VISIBILITY_MAX_CHECKS =
		720;

	function CurrentMatchLaneAdvisorOverlay(
		logger
	) {
		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._renderedPanels =
			[];

		this._visibilityGeneration =
			0;
	}

	function isValidPanel(panel) {
		return !!(
			panel &&
			panel.IsValid()
		);
	}

	function trimText(value) {
		return String(
			value || ''
		).replace(
			/^\s+|\s+$/g,
			''
		);
	}

	CurrentMatchLaneAdvisorOverlay.prototype.render =
		function (
			matches,
			result
		) {
			if (
				!matches ||
				matches.length !==
					EXPECTED_PLAYERS ||
				!result ||
				result.status !==
					'ready' ||
				!result.options ||
				result.options.length !==
					5 ||
				result.localIndex < 0 ||
				result.localIndex >=
					ALLY_PLAYERS
			) {
				this._log(
					'Lane Advisor overlay: invalid render input'
				);

				return false;
			}

			var stayOption =
				this._findOption(
					result.options,
					null
				);

			if (!stayOption) {
				this._log(
					'Lane Advisor overlay: STAY option not found'
				);

				return false;
			}

			var teammateIndex =
				result.localIndex ^
				1;

			var usedPanels =
				[];

			var renderItems =
				[];

			for (
				var allyIndex = 0;
				allyIndex < ALLY_PLAYERS;
				allyIndex++
			) {
				var player =
					matches[
						allyIndex
					];

				if (
					!this._isBindingCurrent(
						player
					)
				) {
					return false;
				}

				if (
					this._containsPanel(
						usedPanels,
						player.panel
					)
				) {
					this._log(
						'Lane Advisor overlay: one player panel is assigned to two ally slots' +
							' | index=' +
							allyIndex
					);

					return false;
				}

				var laneSwapContainer =
					player.panel
						.FindChildTraverse(
							'LaneSwapContainer'
						);

				if (
					!isValidPanel(
						laneSwapContainer
					)
				) {
					this._log(
						'Lane Advisor overlay: LaneSwapContainer not found' +
							' | rosterIndex=' +
							player.rosterIndex
					);

					return false;
				}

				var option =
					allyIndex ===
						result.localIndex ||
					allyIndex ===
						teammateIndex
						? stayOption
						: this._findOption(
							result.options,
							allyIndex
						);

				if (!option) {
					this._log(
						'Lane Advisor overlay: option not found' +
							' | allyIndex=' +
							allyIndex
					);

					return false;
				}

				usedPanels.push(
					player.panel
				);

				renderItems.push({
					player:
						player,

					option:
						option
				});
			}

			this.clear();

			var renderedCount =
				0;

			for (
				var index = 0;
				index < renderItems.length;
				index++
			) {
				var item =
					renderItems[
						index
					];

				if (
					this._renderPlayer(
						item.player,
						item.option
					)
				) {
					renderedCount++;
				}
			}

			this._log(
				'Lane Advisor overlay: render complete' +
					' | rendered=' +
						renderedCount +
					'/' +
						ALLY_PLAYERS +
					' | localIndex=' +
						result.localIndex
			);

			if (
				renderedCount ===
					ALLY_PLAYERS
			) {
				this._startVisibilityMonitor();

				return true;
			}

			return false;
		};

	CurrentMatchLaneAdvisorOverlay.prototype.clear =
		function () {
			this._visibilityGeneration +=
				1;

			var hiddenCount =
				0;

			for (
				var index = 0;
				index <
					this._renderedPanels.length;
				index++
			) {
				var playerPanel =
					this._renderedPanels[
						index
					];

				if (
					!isValidPanel(
						playerPanel
					)
				) {
					continue;
				}

				var overlay =
					playerPanel
						.FindChild(
							OVERLAY_ID
						);

				if (
					!isValidPanel(
						overlay
					)
				) {
					continue;
				}

				if (overlay.visible) {
					hiddenCount++;
				}

				overlay.visible =
					false;
			}

			this._renderedPanels =
				[];

			if (
				hiddenCount >
					0
			) {
				this._log(
					'Lane Advisor overlay: CLEAR' +
						' | hidden=' +
						hiddenCount
				);
			}

			return hiddenCount;
		};

	CurrentMatchLaneAdvisorOverlay.prototype
		._startVisibilityMonitor =
		function () {
			var self =
				this;

			var generation =
				++this._visibilityGeneration;

			var checkCount =
				0;

			var hasSeenAvailable =
				false;

			function checkVisibility() {
				if (
					generation !==
						self._visibilityGeneration
				) {
					return;
				}

				checkCount++;

				var availableCount =
					0;

				var validPanelCount =
					0;

				for (
					var index = 0;
					index <
						self._renderedPanels.length;
					index++
				) {
					var playerPanel =
						self._renderedPanels[
							index
						];

					if (
						!isValidPanel(
							playerPanel
						)
					) {
						continue;
					}

					validPanelCount++;

					if (
						self._isLaneSwapAvailable(
							playerPanel
						)
					) {
						availableCount++;
					}
				}

				if (
					availableCount >
						0
				) {
					hasSeenAvailable =
						true;

					self._setAllVisible(
						true
					);
				} else {
					self._setAllVisible(
						false
					);
				}

				if (
					validPanelCount ===
						0
				) {
					self._log(
						'Lane Advisor overlay: visibility monitor stop' +
							' | reason=no-valid-panels'
					);

					self.clear();

					return;
				}

				if (
					hasSeenAvailable &&
					availableCount ===
						0
				) {
					self._log(
						'Lane Advisor overlay: lane swap window closed'
					);

					self.clear();

					return;
				}

				if (
					checkCount >=
						VISIBILITY_MAX_CHECKS
				) {
					self._log(
						'Lane Advisor overlay: visibility monitor timeout' +
							' | checks=' +
							checkCount
					);

					self.clear();

					return;
				}

				$.Schedule(
					VISIBILITY_CHECK_INTERVAL,

					checkVisibility
				);
			}

			checkVisibility();

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype
		._setAllVisible =
		function (visible) {
			for (
				var index = 0;
				index <
					this._renderedPanels.length;
				index++
			) {
				var playerPanel =
					this._renderedPanels[
						index
					];

				if (
					!isValidPanel(
						playerPanel
					)
				) {
					continue;
				}

				var overlay =
					playerPanel
						.FindChild(
							OVERLAY_ID
						);

				if (
					!isValidPanel(
						overlay
					)
				) {
					continue;
				}

				overlay.visible =
					!!visible;
			}
		};

	CurrentMatchLaneAdvisorOverlay.prototype
		._isLaneSwapAvailable =
		function (playerPanel) {
			if (
				!isValidPanel(
					playerPanel
				)
			) {
				return false;
			}

			try {
				var available =
					playerPanel.BHasClass(
						'LaneSwapAvailable'
					) ||
					playerPanel
						.BAscendantHasClass(
							'LaneSwapAvailable'
						);

				var streetBrawl =
					playerPanel.BHasClass(
						'gamemode_streetbrawl'
					) ||
					playerPanel
						.BAscendantHasClass(
							'gamemode_streetbrawl'
						);

				return (
					available &&
					!streetBrawl
				);
			} catch (
				classError
			) {
				this._log(
					'Lane Advisor overlay: LaneSwapAvailable check error' +
						' | error=' +
							String(
								classError
							)
				);

				return false;
			}
		};

	CurrentMatchLaneAdvisorOverlay.prototype._renderPlayer =
		function (
			player,
			option
		) {
			if (
				!player ||
				!isValidPanel(
					player.panel
				)
			) {
				return false;
			}

			var playerPanel =
				player.panel;

			var overlay =
				playerPanel
					.FindChild(
						OVERLAY_ID
					);

			if (
				!isValidPanel(
					overlay
				)
			) {
				overlay =
					$.CreatePanel(
						'Panel',
						playerPanel,
						OVERLAY_ID
					);

				if (
					!isValidPanel(
						overlay
					)
				) {
					return false;
				}

				overlay.hittest =
					false;

				overlay.hittestchildren =
					false;

				this._configureOverlay(
					overlay
				);

				overlay.visible =
					false;
			}

			var winRateLabel =
				overlay.FindChild(
					WR_LABEL_ID
				);

			if (
				!isValidPanel(
					winRateLabel
				)
			) {
				winRateLabel =
					$.CreatePanel(
						'Label',
						overlay,
						WR_LABEL_ID
					);

				if (
					!isValidPanel(
						winRateLabel
					)
				) {
					return false;
				}

				this._configureLabel(
					winRateLabel,
					3
				);
			}

			var soulsLabel =
				overlay.FindChild(
					SOULS_LABEL_ID
				);

			if (
				!isValidPanel(
					soulsLabel
				)
			) {
				soulsLabel =
					$.CreatePanel(
						'Label',
						overlay,
						SOULS_LABEL_ID
					);

				if (
					!isValidPanel(
						soulsLabel
					)
				) {
					return false;
				}

				this._configureLabel(
					soulsLabel,
					18
				);
			}

			winRateLabel.text =
				this._formatWinRate(
					option
				);

			winRateLabel.style.color =
				this._getWinRateColor(
					option
				);

			soulsLabel.text =
				this._formatSouls15(
					option
				);

			soulsLabel.style.color =
				this._getSouls15Color(
					option
				);

			/*
			 * BEST applies to the entire
			 * data container.
			 *
			 * On every render, always
			 * set both BEST and normal state,
			 * so that after a swap the old purple state
			 * does not remain on another position.
			 */
			if (
				option &&
				option.isBest
			) {
				overlay.style.backgroundColor =
					BEST_BACKGROUND;

				overlay.style.border =
					BEST_BORDER;
			} else {
				overlay.style.backgroundColor =
					OVERLAY_BACKGROUND;

				overlay.style.border =
					OVERLAY_BORDER;
			}

			overlay.visible =
				false;

			this._rememberPanel(
				playerPanel
			);

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._configureOverlay =
		function (overlay) {
			overlay.style.width =
				String(
					OVERLAY_WIDTH
				) +
				'px';

			overlay.style.height =
				String(
					OVERLAY_HEIGHT
				) +
				'px';

			overlay.style.horizontalAlign =
				'center';

			overlay.style.verticalAlign =
				'top';

			overlay.style.position =
				'0px ' +
				String(
					OVERLAY_TOP
				) +
				'px 0px';

			overlay.style.backgroundColor =
				OVERLAY_BACKGROUND;

			overlay.style.border =
				OVERLAY_BORDER;

			overlay.style.zIndex =
				'30';

			overlay.style.overflow =
				'noclip';

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._configureLabel =
		function (
			label,
			top
		) {
			label.hittest =
				false;

			label.style.width =
				'100%';

			label.style.height =
				'15px';

			label.style.horizontalAlign =
				'center';

			label.style.verticalAlign =
				'top';

			label.style.position =
				'0px ' +
				String(
					top
				) +
				'px 0px';

			label.style.textAlign =
				'center';

			label.style.fontSize =
				'10px';

			label.style.fontWeight =
				'bold';

			label.style.fontFamily =
				'block';

			label.style.textOverflow =
				'shrink';

			label.style.textShadow =
				'1px 1px 1px 1.0 #000000';

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._findOption =
		function (
			options,
			swapWithIndex
		) {
			for (
				var index = 0;
				index < options.length;
				index++
			) {
				if (
					options[index]
						.swapWithIndex ===
						swapWithIndex
				) {
					return options[
						index
					];
				}
			}

			return null;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._formatWinRate =
		function (option) {
			if (
				!option ||
				!option.hasMatchData
			) {
				return '— WR n=0';
			}

			return (
				option
					.winRatePercent
					.toFixed(
						1
					) +
				'% WR n=' +
				String(
					option.matches
				)
			);
		};

	CurrentMatchLaneAdvisorOverlay.prototype._formatSouls15 =
		function (option) {
			if (
				!option ||
				!option.hasNetWorthData
			) {
				return '— S15 n=0';
			}

			var rounded =
				Math.round(
					option
						.netWorthDiff15
				);

			return (
				(
					rounded >
						0
						? '+'
						: ''
				) +
				String(
					rounded
				) +
				' S15 n=' +
				String(
					option.netWorthMatches
				)
			);
		};

	CurrentMatchLaneAdvisorOverlay.prototype._getWinRateColor =
		function (option) {
			if (
				!option ||
				!option.hasMatchData
			) {
				return '#D6C06E';
			}

			if (
				option.winRatePercent >=
					55
			) {
				return '#8FE88F';
			}

			if (
				option.winRatePercent <
					45
			) {
				return '#FF9292';
			}

			return '#FFFFFF';
		};

	CurrentMatchLaneAdvisorOverlay.prototype._getSouls15Color =
		function (option) {
			if (
				!option ||
				!option.hasNetWorthData
			) {
				return '#D6C06E';
			}

			if (
				option.netWorthDiff15 >
					0
			) {
				return '#8FE88F';
			}

			if (
				option.netWorthDiff15 <
					0
			) {
				return '#FF9292';
			}

			return '#FFFFFF';
		};

	CurrentMatchLaneAdvisorOverlay.prototype._isBindingCurrent =
		function (player) {
			if (
				!player ||
				!isValidPanel(
					player.panel
				) ||
				!isValidPanel(
					player.playerNameLabel
				) ||
				!isValidPanel(
					player.heroNameLabel
				)
			) {
				return false;
			}

			var currentPlayerName =
				trimText(
					player
						.playerNameLabel
						.text
				);

			var currentHeroName =
				trimText(
					player
						.heroNameLabel
						.text
				);

			if (
				currentPlayerName !==
					player.playerName ||
				currentHeroName !==
					player.heroName
			) {
				this._log(
					'Lane Advisor overlay: binding is stale' +
						' | rosterIndex=' +
							player.rosterIndex +
						' | expectedHero=' +
							player.heroName +
						' | currentHero=' +
							currentHeroName
				);

				return false;
			}

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._rememberPanel =
		function (playerPanel) {
			if (
				!isValidPanel(
					playerPanel
				) ||
				this._containsPanel(
					this._renderedPanels,
					playerPanel
				)
			) {
				return;
			}

			this._renderedPanels.push(
				playerPanel
			);
		};

	CurrentMatchLaneAdvisorOverlay.prototype._containsPanel =
		function (
			panels,
			expected
		) {
			for (
				var index = 0;
				index < panels.length;
				index++
			) {
				if (
					panels[index] ===
						expected
				) {
					return true;
				}
			}

			return false;
		};

	ThreatHud.CurrentMatchLaneAdvisorOverlay =
		CurrentMatchLaneAdvisorOverlay;

})(ThreatHud);