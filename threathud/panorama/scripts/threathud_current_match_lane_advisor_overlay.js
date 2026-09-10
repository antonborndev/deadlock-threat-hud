var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_PLAYERS =
		12;

	var ALLY_PLAYERS =
		6;

	var OVERLAY_ID =
		'ThreatHudLaneAdvisorStats';

	var SOULS_ROW_ID = 'ThreatHudLaneAdvisorSoulsRow';
	var SOULS_ICON_ID = 'ThreatHudLaneAdvisorSoulsIcon';
	var MATCHES_LABEL_ID = 'ThreatHudLaneAdvisorMatches';
	var SOULS_ICON_IMAGE = 's2r://panorama/images/hud/icons/icon_soul.vsvg';

	var SOULS_LABEL_ID =
		'ThreatHudLaneAdvisorSouls15';

	var OVERLAY_TOP =
		125;

	var OVERLAY_WIDTH =
		84;

	var OVERLAY_HEIGHT =
		35;

	var OVERLAY_BACKGROUND =
		'#221c18ee';

	/*
	 * Highlight BEST using the entire block.
	 *
	 * The color is set inline, like the other
	 * working Threat HUD styles.
	 */
	var BEST_BACKGROUND =
		'#B684EB';

	var BACKDROP_ID = 'ThreatHudLaneAdvisorBackdrop';
	var BACKDROP_IMAGE_ID = 'ThreatHudLaneAdvisorBackdropImage';
	var BACKDROP_COLUMNS = 4;
	var BACKDROP_ROWS = 3;
	var BACKDROP_VARIANTS = BACKDROP_COLUMNS * BACKDROP_ROWS;
	var BACKDROP_IMAGE =
		's2r://panorama/images/custom_game/threathud_lane_blocks/block_shapes.vtex';

	var FLAME_ID = 'ThreatHudLaneAdvisorFlame';
	var FLAME_ATLAS_ID = 'ThreatHudLaneAdvisorFlameAtlas';
	var FLAME_FRAME_COUNT = 49;
	var FLAME_ATLAS_COLUMNS = 7;
	var FLAME_ATLAS_ROWS = 7;
	var FLAME_FRAME_INTERVAL = 0.1;
	var FLAME_SIZE = 104;
	var FLAME_OPACITY = '0.8';
	var FLAME_ATLAS_IMAGE =
		's2r://panorama/images/custom_game/threathud_lane_flame/flame_atlas.vtex';

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

		this._flameEntries = [];
		this._flameGeneration = 0;
		this._flameSchedule = null;
		this._flameRunning = false;
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
			this._stopFlameAnimation();
			this._flameEntries = [];

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

			if (visible) {
				this._startFlameAnimation();
			} else {
				this._stopFlameAnimation();
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

			var backdrop = this._prepareBackdrop(overlay);
			if (!isValidPanel(backdrop)) { return false; }

			var soulsRow = overlay.FindChild(SOULS_ROW_ID);
			if (!isValidPanel(soulsRow)) {
				soulsRow = $.CreatePanel('Panel', overlay, SOULS_ROW_ID);
				if (!isValidPanel(soulsRow)) { return false; }
				soulsRow.hittest = false;
				soulsRow.hittestchildren = false;
				soulsRow.style.width = 'fit-children';
				soulsRow.style.maxWidth = '78px';
				soulsRow.style.height = '20px';
				soulsRow.style.horizontalAlign = 'center';
				soulsRow.style.verticalAlign = 'top';
				soulsRow.style.position = '0px 1px 0px';
				soulsRow.style.flowChildren = 'right';
				soulsRow.style.zIndex = '1';
			}

			var soulsLabel = soulsRow.FindChild(SOULS_LABEL_ID);
			if (!isValidPanel(soulsLabel)) {
				soulsLabel = $.CreatePanel('Label', soulsRow, SOULS_LABEL_ID);
				if (!isValidPanel(soulsLabel)) { return false; }
				this._configureLabel(soulsLabel, 0);
				soulsLabel.style.width = 'fit-children';
				soulsLabel.style.maxWidth = '65px';
				soulsLabel.style.height = '20px';
				soulsLabel.style.horizontalAlign = 'left';
				soulsLabel.style.verticalAlign = 'middle';
				soulsLabel.style.fontSize = '16px';
				soulsLabel.style.whiteSpace = 'nowrap';
			}

			var soulsIcon = soulsRow.FindChild(SOULS_ICON_ID);
			if (!isValidPanel(soulsIcon)) {
				soulsIcon = $.CreatePanel('Panel', soulsRow, SOULS_ICON_ID);
				if (!isValidPanel(soulsIcon)) { return false; }
				soulsIcon.hittest = false;
				soulsIcon.style.width = '10px';
				soulsIcon.style.height = '18px';
				soulsIcon.style.marginLeft = '3px';
				soulsIcon.style.verticalAlign = 'middle';
				soulsIcon.style.backgroundImage = 'url("' + SOULS_ICON_IMAGE + '")';
				soulsIcon.style.backgroundSize = 'contain';
				soulsIcon.style.backgroundRepeat = 'no-repeat';
			}

			var matchesLabel = overlay.FindChild(MATCHES_LABEL_ID);
			if (!isValidPanel(matchesLabel)) {
				matchesLabel = $.CreatePanel('Label', overlay, MATCHES_LABEL_ID);
				if (!isValidPanel(matchesLabel)) { return false; }
				this._configureLabel(matchesLabel, 21);
				matchesLabel.style.height = '13px';
				matchesLabel.style.color = '#FFFFFF';
				matchesLabel.style.zIndex = '1';
			}

			var soulsColor = this._getSouls15Color(option);
			soulsLabel.text = this._formatSouls15(option);
			soulsLabel.style.color = soulsColor;
			soulsIcon.style.washColor = soulsColor;
			matchesLabel.text = String(option && option.hasNetWorthData ?
				option.netWorthMatches : 0) + ' M';

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
				backdrop.style.washColor = BEST_BACKGROUND;
				backdrop.style.opacity = FLAME_OPACITY;

				this._prepareFlame(playerPanel, overlay);
			} else {
				backdrop.style.washColor = OVERLAY_BACKGROUND;
				backdrop.style.opacity = '1';
			}

			overlay.visible =
				false;

			this._rememberPanel(
				playerPanel
			);

			return true;
		};

	CurrentMatchLaneAdvisorOverlay.prototype._prepareBackdrop = function (overlay) {
		var viewport = overlay.FindChild(BACKDROP_ID);
		if (!isValidPanel(viewport)) {
			viewport = $.CreatePanel('Panel', overlay, BACKDROP_ID);
			if (!isValidPanel(viewport)) { return null; }
			viewport.hittest = false;
			viewport.hittestchildren = false;
			viewport.style.width = OVERLAY_WIDTH + 'px';
			viewport.style.height = OVERLAY_HEIGHT + 'px';
			viewport.style.position = '0px 0px 0px';
			viewport.style.overflow = 'clip clip';
			viewport.style.flowChildren = 'none';
			viewport.style.zIndex = '0';
			// Pick once per block; repeated results keep the same shape.
			viewport.SetAttributeInt('threathud_lane_block_variant',
				Math.floor(Math.random() * BACKDROP_VARIANTS));
		}
		var image = viewport.FindChild(BACKDROP_IMAGE_ID);
		if (!isValidPanel(image)) {
			image = $.CreatePanel('Image', viewport, BACKDROP_IMAGE_ID);
			if (!isValidPanel(image)) { return null; }
			image.hittest = false;
			image.style.width = (OVERLAY_WIDTH * BACKDROP_COLUMNS) + 'px';
			image.style.height = (OVERLAY_HEIGHT * BACKDROP_ROWS) + 'px';
			image.style.horizontalAlign = 'left';
			image.style.verticalAlign = 'top';
			var variant = viewport.GetAttributeInt('threathud_lane_block_variant', 0);
			image.style.position = (-(variant % BACKDROP_COLUMNS) * OVERLAY_WIDTH) +
				'px ' + (-Math.floor(variant / BACKDROP_COLUMNS) * OVERLAY_HEIGHT) + 'px 0px';
			image.SetImage(BACKDROP_IMAGE);
		}
		return image;
	};

	CurrentMatchLaneAdvisorOverlay.prototype._prepareFlame = function (playerPanel, overlay) {
		try {
			var image = playerPanel.FindChild(FLAME_ID);
			if (!isValidPanel(image)) {
				image = $.CreatePanel('Panel', playerPanel, FLAME_ID);
			}
			if (!isValidPanel(image)) { return; }
			image.visible = false;
			image.hittest = false;
			image.hittestchildren = false;
			image.style.width = FLAME_SIZE + 'px';
			image.style.height = FLAME_SIZE + 'px';
			image.style.horizontalAlign = 'center';
			image.style.verticalAlign = 'top';
			// The prepared 400x400 canvas places the card anchor at (124,366).
			// Align that point with the centre/top of this existing 84px block.
			image.style.position = (FLAME_SIZE * (0.5 - 124 / 400)).toFixed(2) +
				'px ' + (OVERLAY_TOP + 5 - FLAME_SIZE * 366 / 400).toFixed(2) + 'px 0px';
			image.style.zIndex = '29';
			image.style.opacity = FLAME_OPACITY;
			image.style.overflow = 'clip clip';
			image.style.flowChildren = 'none';
			var atlasImage = image.FindChild(FLAME_ATLAS_ID);
			if (!isValidPanel(atlasImage)) {
				atlasImage = $.CreatePanel('Image', image, FLAME_ATLAS_ID);
			}
			if (!isValidPanel(atlasImage)) { return; }
			atlasImage.hittest = false;
			atlasImage.hittestchildren = false;
			atlasImage.style.width = (FLAME_SIZE * FLAME_ATLAS_COLUMNS) + 'px';
			atlasImage.style.height = (FLAME_SIZE * FLAME_ATLAS_ROWS) + 'px';
			atlasImage.style.horizontalAlign = 'left';
			atlasImage.style.verticalAlign = 'top';
			atlasImage.style.position = '0px 0px 0px';
			// Keep the same resource attached across frames and recommendation renders.
			if (atlasImage.GetAttributeString('threathud_flame_atlas', '') !== FLAME_ATLAS_IMAGE) {
				atlasImage.SetImage(FLAME_ATLAS_IMAGE);
				atlasImage.SetAttributeString('threathud_flame_atlas', FLAME_ATLAS_IMAGE);
			}
			this._flameEntries.push({ playerPanel: playerPanel, overlay: overlay,
				image: image, atlasImage: atlasImage, frame: -1, failed: false });
		} catch (error) {
			this._log('Lane Advisor flame: prepare failed | error=' + String(error));
		}
	};

	CurrentMatchLaneAdvisorOverlay.prototype._isFlameVisible = function (entry) {
		if (entry.failed || !isValidPanel(entry.image) || !isValidPanel(entry.atlasImage) ||
			!isValidPanel(entry.overlay) ||
			!isValidPanel(entry.playerPanel) || !entry.overlay.visible ||
			!this._isLaneSwapAvailable(entry.playerPanel)) {
			return false;
		}
		// Also honour a hidden scoreboard/HUD ancestor without another poller.
		var panel = entry.playerPanel;
		for (var depth = 0; depth < 32 && isValidPanel(panel); depth++) {
			if (panel.visible === false) { return false; }
			if (typeof panel.GetParent !== 'function') { break; }
			panel = panel.GetParent();
		}
		return true;
	};

	CurrentMatchLaneAdvisorOverlay.prototype._startFlameAnimation = function () {
		if (this._flameRunning || this._flameEntries.length === 0) { return; }
		var hasVisible = false;
		for (var i = 0; i < this._flameEntries.length; i++) {
			if (this._isFlameVisible(this._flameEntries[i])) { hasVisible = true; break; }
		}
		if (!hasVisible) { return; }
		var self = this;
		var generation = ++this._flameGeneration;
		var frame = 0;
		this._flameRunning = true;
		this._log('Lane Advisor flame: START | panels=' + this._flameEntries.length +
			' | frames=' + FLAME_FRAME_COUNT + ' | fps=10 | mode=atlas');

		function tick() {
			if (!self._flameRunning || generation !== self._flameGeneration) { return; }
			self._flameSchedule = null;
			var visibleCount = 0;
			for (var index = 0; index < self._flameEntries.length; index++) {
				var entry = self._flameEntries[index];
				if (!isValidPanel(entry.image)) { continue; }
				if (!self._isFlameVisible(entry)) { entry.image.visible = false; continue; }
				try {
					if (entry.frame !== frame) {
						var column = frame % FLAME_ATLAS_COLUMNS;
						var row = Math.floor(frame / FLAME_ATLAS_COLUMNS);
						entry.atlasImage.style.position = (-column * FLAME_SIZE) + 'px ' +
							(-row * FLAME_SIZE) + 'px 0px';
						entry.frame = frame;
					}
					entry.image.visible = true;
					visibleCount++;
				} catch (error) {
					entry.failed = true;
					entry.image.visible = false;
					self._log('Lane Advisor flame: image failed | error=' + String(error));
				}
			}
			if (visibleCount === 0) { self._stopFlameAnimation(); return; }
			frame = (frame + 1) % FLAME_FRAME_COUNT;
			self._flameSchedule = $.Schedule(FLAME_FRAME_INTERVAL, tick);
		}
		tick();
	};

	CurrentMatchLaneAdvisorOverlay.prototype._stopFlameAnimation = function () {
		this._flameRunning = false;
		this._flameGeneration++;
		if (this._flameSchedule !== null && typeof $.CancelScheduled === 'function') {
			try { $.CancelScheduled(this._flameSchedule); } catch (error) {}
		}
		this._flameSchedule = null;
		for (var index = 0; index < this._flameEntries.length; index++) {
			var image = this._flameEntries[index].image;
			if (isValidPanel(image)) { image.visible = false; }
		}
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

			overlay.style.backgroundColor = '#00000000';
			overlay.style.border = '0px solid #00000000';

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
				'VALVEOracle';

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

	CurrentMatchLaneAdvisorOverlay.prototype._formatSouls15 =
		function (option) {
			if (
				!option ||
				!option.hasNetWorthData
			) {
				return '—';
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
				)
			);
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
				return '#E86A62';
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
