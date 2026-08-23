var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var TEAM_ALLY =
		'ally';

	var TEAM_ENEMY =
		'enemy';

	var PLAYERS_PER_TEAM =
		6;

	var TOTAL_PLAYERS =
		12;

	function MatchRoster(
		topPanel,
		logger
	) {
		this._topPanel =
			topPanel;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		/*
		 * TeamsContainer is searched for only once.
		 *
		 * As long as this panel remains valid,
		 * no full topPanel traversal is performed again.
		 */
		this._teamsRoot =
			null;

		this._teams = {
			ally:
				null,

			enemy:
				null
		};

		this._containers = {
			ally:
				null,

			enemy:
				null
		};

		/*
		 * Persistent references for the current game session:
		 *
		 * - player panel;
		 * - PlayerName Label;
		 * - HeroName Label.
		 *
		 * These references are created once after the
		 * full roster appears and are reused afterward.
		 */
		this._bindings =
			[];

		this._players =
			[];
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

	/*
	* A Steam player name may contain virtually
	* any displayable characters:
	*
	* # { } emoji, etc.
	*
	* After trimText(), the only requirement for the name is
	* that it is not empty.
	*/
	function isUsablePlayerName(text) {
		return !!text;
	}

	/*
	* HeroName is native game text.
	*
	* Keep strict validation here so that
	* an unresolved localization token or placeholder
	* is not mistaken for a hero name.
	*/
	function isUsableHeroName(text) {
		return (
			!!text &&
			text !== 'undefined' &&
			text !== 'null' &&
			text.charAt(0) !== '#' &&
			text.indexOf('{') === -1 &&
			text.indexOf('}') === -1
		);
	}

	/*
	 * Resets current roster data,
	 * but keeps TeamsContainer.
	 *
	 * This avoids searching for TeamsContainer across the entire HUD
	 * again during temporary team loading.
	 */
	MatchRoster.prototype
		._clearConnection =
		function () {
			this._teams.ally =
				null;

			this._teams.enemy =
				null;

			this._containers.ally =
				null;

			this._containers.enemy =
				null;

			this._bindings =
				[];

			this._players =
				[];
		};

	/*
	 * Full invalidation when the screen changes.
	 *
	 * After this, the next connect() will perform
	 * one new TeamsContainer discovery.
	 */
MatchRoster.prototype.invalidate =
	function () {
		/*
		 * Completely reset only the data
		 * for the specific game session.
		 *
		 * The next connect() will find again
		 * the active team panels.
		 */
		this._clearConnection();
	};

	/*
	 * Validates the already stored connection.
	 *
	 * There is no tree traversal here:
	 * only checks of existing references
	 * and the enemy runtime class.
	 */
	MatchRoster.prototype
	._isConnectionValid =
	function () {
		return (
			isValidPanel(
				this._teams.ally
			) &&
			isValidPanel(
				this._teams.enemy
			) &&
			isValidPanel(
				this._containers.ally
			) &&
			isValidPanel(
				this._containers.enemy
			) &&
			!this._teams.ally
				.BHasClass(
					'enemy'
				) &&
			this._teams.enemy
				.BHasClass(
					'enemy'
				)
		);
	};
	/*
	 * Connects to both teams.
	 *
	 * Main difference from the old version:
	 *
	 * before:
	 *     each call ran findPanelsByType()
	 *     from the HUD root;
	 *
	 * now:
	 *     1. TeamsContainer is found once;
	 *     2. TeamFriendly and TeamEnemy are taken
	 *        as direct children by ID;
	 *     3. the side is determined by the enemy class.
	 *
	 * IDs are used only for fast discovery.
	 * They do not determine which team is allied.
	 */
	MatchRoster.prototype.connect =
	function () {
		/*
		 * If the active panels have already been found successfully
		 * and are still valid, no tree traversal is performed.
		 */
		if (
			this._isConnectionValid()
		) {
			return true;
		}

		/*
		 * The old connection can no longer be used.
		 *
		 * Important: no panel found during a failed
		 * attempt is cached anymore.
		 */
		this._clearConnection();

		if (
			!isValidPanel(
				this._topPanel
			)
		) {
			return false;
		}

		/*
		 * A full traversal is performed only when
		 * there is no working connection yet.
		 *
		 * After a successful connection, the next call
		 * will return above via _isConnectionValid().
		 */
		var teamPanels =
			ThreatHud.PanelUtils
				.findPanelsByType(
					this._topPanel,
					'CitadelHudTopBarTeam'
				);

		var enemyCandidates =
			[];

		var allyCandidates =
			[];

		for (
			var index = 0;
			index < teamPanels.length;
			index++
		) {
			var teamPanel =
				teamPanels[index];

			if (
				!isValidPanel(
					teamPanel
				)
			) {
				continue;
			}

			/*
			 * Restrict the search to native team panels.
			 *
			 * ID does not determine the team side:
			 * it is used only as a check
			 * that this is the required topbar element.
			 */
			if (
				teamPanel.id !==
					'TeamFriendly' &&
				teamPanel.id !==
					'TeamEnemy'
			) {
				continue;
			}

			var playersContainer =
				teamPanel.FindChildTraverse(
					'PlayersContainer'
				);

			if (
				!isValidPanel(
					playersContainer
				)
			) {
				continue;
			}

			var candidate = {
				panel:
					teamPanel,

				container:
					playersContainer
			};

			/*
			 * The actual side is determined only
			 * by the enemy runtime class.
			 *
			 * This behavior has already been confirmed:
			 * TeamFriendly is sometimes the enemy
			 * panel and receives the enemy class.
			 */
			if (
				teamPanel.BHasClass(
					'enemy'
				)
			) {
				enemyCandidates.push(
					candidate
				);
			} else {
				allyCandidates.push(
					candidate
				);
			}
		}

		/*
		 * During loading there may be:
		 *
		 * enemy=0, ally=2
		 *
		 * In this case, cache nothing.
		 * The next discovery attempt will inspect again
		 * the current tree and can find the new topbar.
		 */
		if (
			enemyCandidates.length !== 1 ||
			allyCandidates.length !== 1
		) {
			this._log(
				'MatchRoster: teams are not ready yet' +
					' | teamPanels=' +
					teamPanels.length +
					' | enemyCandidates=' +
					enemyCandidates.length +
					' | allyCandidates=' +
					allyCandidates.length
			);

			return false;
		}

		/*
		 * Caching is performed only after
		 * exactly one allied and one
		 * enemy runtime team have been found.
		 */
		this._teams.ally =
			allyCandidates[0].panel;

		this._containers.ally =
			allyCandidates[0].container;

		this._teams.enemy =
			enemyCandidates[0].panel;

		this._containers.enemy =
			enemyCandidates[0].container;

		this._log(
			'MatchRoster: teams connected' +
				' | allyId=' +
				(
					this._teams.ally.id ||
					'<empty>'
				) +
				' | enemyId=' +
				(
					this._teams.enemy.id ||
					'<empty>'
				)
		);

		return true;
	};

	/*
	 * Returns player panels directly
	 * from PlayersContainer.
	 *
	 * The method is used only during initial
	 * binding-cache construction.
	 */
	MatchRoster.prototype
		._getDirectPlayerPanels =
		function (container) {
			var result =
				[];

			if (
				!isValidPanel(
					container
				)
			) {
				return result;
			}

			var children =
				container.Children();

			/*
			 * Primary path:
			 * the native panel reports the correct paneltype.
			 */
			for (
				var index = 0;
				index < children.length;
				index++
			) {
				var child =
					children[index];

				if (
					isValidPanel(
						child
					) &&
					child.paneltype ===
						'CitadelHudTopBarPlayer'
				) {
					result.push(
						child
					);
				}
			}

			if (result.length > 0) {
				return result;
			}

			/*
			 * Fallback is performed only once
			 * when a new roster is discovered.
			 */
			for (
				var fallbackIndex = 0;
				fallbackIndex <
					children.length;
				fallbackIndex++
			) {
				var fallbackChild =
					children[
						fallbackIndex
					];

				if (
					!isValidPanel(
						fallbackChild
					)
				) {
					continue;
				}

				var heroImageArea =
					fallbackChild
						.FindChildTraverse(
							'HeroImageArea'
						);

				if (
					isValidPanel(
						heroImageArea
					)
				) {
					result.push(
						fallbackChild
					);
				}
			}

			return result;
		};

	/*
	 * Finds the panels and labels of one team once.
	 *
	 * After that, PlayerName/HeroName are no longer
	 * searched recursively until the session changes.
	 */
	MatchRoster.prototype
		._buildTeamBindings =
		function (
			team,
			rosterOffset
		) {
			var panels =
				this._getDirectPlayerPanels(
					this._containers[
						team
					]
				);

			if (
				panels.length !==
					PLAYERS_PER_TEAM
			) {
				return null;
			}

			var bindings =
				[];

			for (
				var teamIndex = 0;
				teamIndex < panels.length;
				teamIndex++
			) {
				var playerPanel =
					panels[
						teamIndex
					];

				/*
				 * These two recursive searches are performed
				 * only once per game session.
				 */
				var playerNameLabel =
					ThreatHud.PanelUtils
						.findFirstLabelByClass(
							playerPanel,
							'PlayerName'
						);

				var heroNameLabel =
					ThreatHud.PanelUtils
						.findFirstLabelByClass(
							playerPanel,
							'HeroName'
						);

				if (
					!isValidPanel(
						playerNameLabel
					) ||
					!isValidPanel(
						heroNameLabel
					)
				) {
					return null;
				}

				bindings.push({
					rosterIndex:
						rosterOffset +
						teamIndex,

					team:
						team,

					teamIndex:
						teamIndex,

					panel:
						playerPanel,

					playerNameLabel:
						playerNameLabel,

					heroNameLabel:
						heroNameLabel
				});
			}

			return bindings;
		};

	/*
	 * Creates a full cache of 12 slots.
	 */
	MatchRoster.prototype
		._buildBindings =
		function () {
			var allyBindings =
				this._buildTeamBindings(
					TEAM_ALLY,
					0
				);

			if (!allyBindings) {
				return false;
			}

			var enemyBindings =
				this._buildTeamBindings(
					TEAM_ENEMY,
					PLAYERS_PER_TEAM
				);

			if (!enemyBindings) {
				return false;
			}

			this._bindings =
				allyBindings.concat(
					enemyBindings
				);

			return (
				this._bindings.length ===
					TOTAL_PLAYERS
			);
		};

	/*
	 * Cheap validation of the existing cache.
	 *
	 * No searches are performed here.
	 */
	MatchRoster.prototype
		._areBindingsValid =
		function () {
			if (
				this._bindings.length !==
					TOTAL_PLAYERS
			) {
				return false;
			}

			if (
				!this._isConnectionValid()
			) {
				return false;
			}

			for (
				var index = 0;
				index < this._bindings.length;
				index++
			) {
				var binding =
					this._bindings[index];

				if (
					!isValidPanel(
						binding.panel
					) ||
					!isValidPanel(
						binding.playerNameLabel
					) ||
					!isValidPanel(
						binding.heroNameLabel
					)
				) {
					return false;
				}
			}

			return true;
		};

	/*
	 * Reads one already cached slot.
	 *
	 * This is the main method used by the persistent watcher.
	 *
	 * Cost:
	 * - three IsValid() calls;
	 * - two .text reads;
	 * - no tree traversal.
	 */
	MatchRoster.prototype
	.readCachedPlayer =
	function (rosterIndex) {
		if (
			rosterIndex < 0 ||
			rosterIndex >=
				this._bindings.length
		) {
			return null;
		}

		var binding =
			this._bindings[
				rosterIndex
			];

		if (
			!isValidPanel(
				binding.panel
			) ||
			!isValidPanel(
				binding.playerNameLabel
			) ||
			!isValidPanel(
				binding.heroNameLabel
			)
		) {
			return null;
		}

		var playerName =
			trimText(
				binding
					.playerNameLabel
					.text
			);

		var heroName =
			trimText(
				binding
					.heroNameLabel
					.text
			);

		if (
			!isUsablePlayerName(
				playerName
			) ||
			!isUsableHeroName(
				heroName
			)
		) {
			return null;
		}

		return {
			rosterIndex:
				binding.rosterIndex,

			team:
				binding.team,

			teamIndex:
				binding.teamIndex,

			playerName:
				playerName,

			heroName:
				heroName,

			/*
			 * These references are not sent to Bridge.
			 *
			 * They remain inside Panorama and allow
			 * the API response to be bound to a specific panel,
			 * rather than to a potentially stale index.
			 */
			panel:
				binding.panel,

			playerNameLabel:
				binding.playerNameLabel,

			heroNameLabel:
				binding.heroNameLabel
		};
	};
	
		/*
 * Finds a binding by the stored reference
 * to the native player panel.
 *
 * Used only for temporary validation
 * of the current player order.
 */
MatchRoster.prototype
	._findBindingIndexByPanel =
	function (panel) {
		for (
			var index = 0;
			index < this._bindings.length;
			index++
		) {
			if (
				this._bindings[index]
					.panel === panel
			) {
				return index;
			}
		}

		return -1;
	};

/*
 * Returns the roster in the current live order
 * of panels inside PlayersContainer.
 *
 * There is no full HUD traversal here.
 *
 * Only the following is performed:
 *
 * - Children() for the two PlayersContainer panels;
 * - matching panel references to ready bindings;
 * - reading already cached PlayerName/HeroName labels.
 *
 * The method can detect when:
 *
 * - players swapped lanes;
 * - native player panels changed order;
 * - the hero changed inside an existing panel;
 * - the player name changed;
 * - one of the old panels was replaced with a new one.
 */
MatchRoster.prototype
	.readLiveOrderedSnapshot =
	function () {
		if (
			!this._areBindingsValid()
		) {
			return null;
		}

		var allyPanels =
			this._getDirectPlayerPanels(
				this._containers[
					TEAM_ALLY
				]
			);

		var enemyPanels =
			this._getDirectPlayerPanels(
				this._containers[
					TEAM_ENEMY
				]
			);

		if (
			allyPanels.length !==
				PLAYERS_PER_TEAM ||
			enemyPanels.length !==
				PLAYERS_PER_TEAM
		) {
			return null;
		}

		/*
		 * This is the actual current order of child
		 * panels in the UI.
		 */
		var panels =
			allyPanels.concat(
				enemyPanels
			);

		var players =
			[];

		var fingerprintParts =
			[];

		for (
			var liveIndex = 0;
			liveIndex < panels.length;
			liveIndex++
		) {
			var panel =
				panels[
					liveIndex
				];

			/*
			 * The binding stores references to labels
			 * found during initial
			 * roster construction.
			 */
			var bindingIndex =
				this._findBindingIndexByPanel(
					panel
				);

			/*
			 * A new panel appeared in the UI
			 * that is not present in the old cache.
			 */
			if (
				bindingIndex < 0
			) {
				return null;
			}

			var cachedPlayer =
				this.readCachedPlayer(
					bindingIndex
				);

			if (!cachedPlayer) {
				return null;
			}

			var team =
				liveIndex <
					PLAYERS_PER_TEAM
						? TEAM_ALLY
						: TEAM_ENEMY;

			var teamIndex =
				liveIndex <
					PLAYERS_PER_TEAM
						? liveIndex
						: liveIndex -
							PLAYERS_PER_TEAM;

			var player = {
				/*
				 * Indexes are recalculated
				 * from the current live order.
				 */
				rosterIndex:
					liveIndex,

				team:
					team,

				teamIndex:
					teamIndex,

				playerName:
					cachedPlayer
						.playerName,

				heroName:
					cachedPlayer
						.heroName,

				panel:
					panel,

				playerNameLabel:
					cachedPlayer
						.playerNameLabel,

				heroNameLabel:
					cachedPlayer
						.heroNameLabel
			};

			players.push(
				player
			);

			fingerprintParts.push(
				player.team +
					':' +
					player.teamIndex +
					':' +
					player.playerName +
					':' +
					player.heroName
			);
		}

		return {
			players:
				players,

			panels:
				panels,

			fingerprint:
				fingerprintParts.join(
					'|'
				)
		};
	};

	/*
	 * Reads all 12 values from already found labels.
	 *
	 * This method is needed by the matcher when starting
	 * stats, but no longer performs 24 searches.
	 */
	MatchRoster.prototype.readPlayers =
		function () {
			if (
				!this.connect()
			) {
				return false;
			}

			if (
				!this._areBindingsValid()
			) {
				this._bindings =
					[];

				if (
					!this._buildBindings()
				) {
					return false;
				}
			}

			var players =
				[];

			for (
				var index = 0;
				index < TOTAL_PLAYERS;
				index++
			) {
				var player =
					this.readCachedPlayer(
						index
					);

				if (!player) {
					return false;
				}

				players.push(
					player
				);
			}

			this._players =
				players;

			return true;
		};

	/*
	 * Returns player panels.
	 *
	 * If bindings are already ready, no
	 * Children() calls or searches are performed.
	 */
	MatchRoster.prototype
		.getPlayerPanels =
		function (team) {
			var result =
				[];

			if (
				this._bindings.length ===
					TOTAL_PLAYERS
			) {
				for (
					var index = 0;
					index <
						this._bindings.length;
					index++
				) {
					if (
						this._bindings[index]
							.team === team
					) {
						result.push(
							this._bindings[index]
								.panel
						);
					}
				}

				return result;
			}

			return this._getDirectPlayerPanels(
				this._containers[
					team
				]
			);
		};

	/*
	 * Returns 12 cached panel references.
	 */
	MatchRoster.prototype
		.getCachedPanelReferences =
		function () {
			var result =
				[];

			if (
				this._bindings.length !==
					TOTAL_PLAYERS
			) {
				return result;
			}

			for (
				var index = 0;
				index < this._bindings.length;
				index++
			) {
				result.push(
					this._bindings[index]
						.panel
				);
			}

			return result;
		};

	MatchRoster.prototype.getPlayers =
	function () {
		var result =
			[];

		for (
			var index = 0;
			index < this._players.length;
			index++
		) {
			var player =
				this._players[index];

			result.push({
				rosterIndex:
					player.rosterIndex,

				team:
					player.team,

				teamIndex:
					player.teamIndex,

				playerName:
					player.playerName,

				heroName:
					player.heroName,

				/*
				 * Panorama references.
				 *
				 * They are needed only inside the client
				 * and are not serialized into the HTTP request.
				 */
				panel:
					player.panel,

				playerNameLabel:
					player.playerNameLabel,

				heroNameLabel:
					player.heroNameLabel
			});
		}

		return result;
	};

	MatchRoster.prototype.getFingerprint =
		function () {
			var parts =
				[];

			for (
				var index = 0;
				index < this._players.length;
				index++
			) {
				var player =
					this._players[index];

				parts.push(
					player.team +
						':' +
						player.teamIndex +
						':' +
						player.playerName +
						':' +
						player.heroName
				);
			}

			return parts.join(
				'|'
			);
		};

	ThreatHud.MatchRoster =
		MatchRoster;

})(ThreatHud);