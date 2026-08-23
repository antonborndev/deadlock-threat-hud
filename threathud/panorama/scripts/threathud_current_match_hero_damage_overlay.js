var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var TEAM_ALLY =
        'ally';

    var TEAM_ENEMY =
        'enemy';

    var CONTAINER_ID =
        'ThreatHudHeroDamageContainer';

    var CAPTION_ID =
        'ThreatHudHeroDamageCaption';

    var VALUE_ID =
        'ThreatHudHeroDamageValue';

    var FLOW_CLASS =
        'LeftRightFlow';

    var OWN_CLASS =
        'ThreatHudHeroDamage';

    var DAMAGE_COLOR_PURPLE =
        '#800080';

    var DAMAGE_COLOR_GREEN =
        '#008000';

    var DAMAGE_COLOR_WHITE =
        '#FFFFFF';

    var DAMAGE_COLOR_RED =
        '#FF0000';

    var UiUtils =
        ThreatHud.CurrentMatchUiUtils;

    var isValidPanel =
        UiUtils.isValidPanel;

    function CurrentMatchHeroDamageOverlay(
        logger
    ) {
        this._log =
            typeof logger ===
                'function'
                    ? logger
                    : function () {};

        this._bindings =
            [];
    }

    CurrentMatchHeroDamageOverlay.prototype.bind =
        function (
            context
        ) {
            this.unbind();

            if (
                !context ||
                !UiUtils.validatePlayers(
                    context.players,
                    EXPECTED_PLAYERS,
                    this._log,
                    'Hero damage overlay'
                ) ||
                !context.panels ||
                context.panels.length !==
                    EXPECTED_PLAYERS
            ) {
                return false;
            }

            var discovered =
                [];

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var player =
                    context.players[index];

                var playerPanel =
                    context.panels[index];

                if (
                    player.rosterIndex !==
                        index ||
                    (
                        player.team !==
                            TEAM_ALLY &&
                        player.team !==
                            TEAM_ENEMY
                    ) ||
                    player.panel !==
                        playerPanel ||
                    !isValidPanel(
                        playerPanel
                    )
                ) {
                    this._log(
                        'Hero damage overlay: cached panel mismatch' +
                            ' | rosterIndex=' +
                            index
                    );

                    return false;
                }

                /*
                 * This is the only subtree lookup performed for
                 * this player during the current roster generation.
                 */
                var kdaContainer =
                    playerPanel.FindChildTraverse(
                        'KDAContainer'
                    );

                if (
                    !isValidPanel(
                        kdaContainer
                    )
                ) {
                    this._log(
                        'Hero damage overlay: KDAContainer not found' +
                            ' | rosterIndex=' +
                            index
                    );

                    return false;
                }

                discovered.push({
                    rosterIndex:
                        index,

                    team:
                        player.team,

                    playerPanel:
                        playerPanel,

                    kdaContainer:
                        kdaContainer
                });
            }

            var bindings =
                [];

            for (
                var bindingIndex = 0;
                bindingIndex <
                    discovered.length;
                bindingIndex += 1
            ) {
                var discoveredBinding =
                    discovered[
                        bindingIndex
                    ];

                var binding =
                    this._createBinding(
                        discoveredBinding
                    );

                if (!binding) {
                    this._bindings =
                        bindings;

                    this.unbind();

                    this._log(
                        'Hero damage overlay: failed to create controls' +
                            ' | rosterIndex=' +
                            bindingIndex
                    );

                    return false;
                }

                bindings.push(
                    binding
                );
            }

            this._bindings =
                bindings;

            this.clearValues();

            this._log(
                'Hero damage overlay: BOUND' +
                    ' | players=' +
                    bindings.length
            );

            return true;
        };

    CurrentMatchHeroDamageOverlay.prototype.apply =
        function (
            entries
        ) {
            if (
                !entries ||
                entries.length !==
                    EXPECTED_PLAYERS ||
                this._bindings.length !==
                    EXPECTED_PLAYERS
            ) {
                return false;
            }

            for (
                var validateIndex = 0;
                validateIndex <
                    EXPECTED_PLAYERS;
                validateIndex += 1
            ) {
                var validateBinding =
                    this._bindings[
                        validateIndex
                    ];

                var validateEntry =
                    entries[
                        validateIndex
                    ];

                if (
                    !validateBinding ||
                    !validateEntry ||
                    validateBinding.rosterIndex !==
                        validateIndex ||
                    validateEntry.rosterIndex !==
                        validateIndex ||
                    typeof validateEntry.present !==
                        'boolean' ||
                    (
                        validateEntry.present &&
                        !this._isValidDamage(
                            validateEntry.heroDamage
                        )
                    ) ||
                    !isValidPanel(
                        validateBinding.playerPanel
                    ) ||
                    !isValidPanel(
                        validateBinding.kdaContainer
                    ) ||
                    !isValidPanel(
                        validateBinding.container
                    ) ||
                    !isValidPanel(
                        validateBinding.valueLabel
                    )
                ) {
                    this._log(
                        'Hero damage overlay: cached binding is invalid' +
                            ' | rosterIndex=' +
                            validateIndex
                    );

                    return false;
                }
            }

            var presentCount =
                0;

            var changedCount =
                0;

            /*
             * Merge the new snapshot into the values already shown for
             * this roster generation. Bridge reconnects keep their sticky
             * records, while a temporarily absent entry must not make the
             * remaining team change colour against an incomplete baseline.
             */
            for (
                var mergeIndex = 0;
                mergeIndex < EXPECTED_PLAYERS;
                mergeIndex += 1
            ) {
                var mergeEntry =
                    entries[mergeIndex];

                if (mergeEntry.present) {
                    this._bindings[
                        mergeIndex
                    ].heroDamage =
                        mergeEntry.heroDamage;
                }
            }

            var damageColors =
                this._calculateDamageColors();

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                var binding =
                    this._bindings[index];

                if (
                    !this._isValidDamage(
                        binding.heroDamage
                    )
                ) {
                    continue;
                }

                var damageText =
                    String(
                        binding.heroDamage
                    );

                if (
                    binding.valueLabel.text !==
                        damageText
                ) {
                    binding.valueLabel.text =
                        damageText;

                    changedCount +=
                        1;
                }

                var damageColor =
                    damageColors[index];

                if (
                    binding.damageColor !==
                        damageColor
                ) {
                    binding.valueLabel.style.color =
                        damageColor;

                    binding.damageColor =
                        damageColor;

                    changedCount +=
                        1;
                }

                if (
                    !binding.container.visible
                ) {
                    binding.container.visible =
                        true;

                    changedCount +=
                        1;
                }

                presentCount +=
                    1;
            }

            if (
                changedCount > 0
            ) {
                this._log(
                    'Hero damage overlay: UPDATE' +
                        ' | present=' +
                        presentCount +
                        '/' +
                        EXPECTED_PLAYERS +
                        ' | changes=' +
                        changedCount
                );
            }

            return true;
        };

    CurrentMatchHeroDamageOverlay.prototype.clearValues =
        function () {
            var indexes =
                [];

            for (
                var index = 0;
                index < this._bindings.length;
                index += 1
            ) {
                indexes.push(
                    index
                );
            }

            return this.clearSlots(
                indexes
            );
        };

    CurrentMatchHeroDamageOverlay.prototype.clearSlots =
        function (
            indexes
        ) {
            if (!indexes) {
                return 0;
            }

            var clearedCount =
                0;

            for (
                var index = 0;
                index < indexes.length;
                index += 1
            ) {
                var rosterIndex =
                    indexes[index];

                if (
                    rosterIndex < 0 ||
                    rosterIndex >=
                        this._bindings.length
                ) {
                    continue;
                }

                var binding =
                    this._bindings[
                        rosterIndex
                    ];

                if (
                    !binding ||
                    !isValidPanel(
                        binding.container
                    ) ||
                    !isValidPanel(
                        binding.valueLabel
                    )
                ) {
                    continue;
                }

                if (
                    binding.container.visible
                ) {
                    clearedCount +=
                        1;
                }

                if (
                    binding.valueLabel.text !==
                        ''
                ) {
                    binding.valueLabel.text =
                        '';
                }

                binding.heroDamage =
                    null;

                if (
                    binding.damageColor !==
                        DAMAGE_COLOR_WHITE
                ) {
                    binding.valueLabel.style.color =
                        DAMAGE_COLOR_WHITE;

                    binding.damageColor =
                        DAMAGE_COLOR_WHITE;
                }

                if (
                    binding.container.visible
                ) {
                    binding.container.visible =
                        false;
                }
            }

            return clearedCount;
        };

    CurrentMatchHeroDamageOverlay.prototype.unbind =
        function () {
            var hadBindings =
                this._bindings.length >
                    0;

            this.clearValues();

            this._bindings =
                [];

            if (hadBindings) {
                this._log(
                    'Hero damage overlay: UNBOUND'
                );
            }

            return hadBindings;
        };

    CurrentMatchHeroDamageOverlay.prototype._createBinding =
        function (
            discovered
        ) {
            var kdaContainer =
                discovered.kdaContainer;

            var container =
                kdaContainer.FindChild(
                    CONTAINER_ID
                );

            if (
                !isValidPanel(
                    container
                )
            ) {
                container =
                    $.CreatePanel(
                        'Panel',
                        kdaContainer,
                        CONTAINER_ID
                    );
            }

            if (
                !isValidPanel(
                    container
                )
            ) {
                return null;
            }

            container.AddClass(
                FLOW_CLASS
            );

            container.AddClass(
                OWN_CLASS
            );

            this._configureContainer(
                container
            );

            var captionLabel =
                this._getOrCreateLabel(
                    container,
                    CAPTION_ID
                );

            var valueLabel =
                this._getOrCreateLabel(
                    container,
                    VALUE_ID
                );

            if (
                !captionLabel ||
                !valueLabel
            ) {
                container.visible =
                    false;

                return null;
            }

            this._configureCaptionLabel(
                captionLabel
            );

            this._configureValueLabel(
                valueLabel
            );

            captionLabel.text =
                'Hero Dmg:';

            valueLabel.text =
                '';

            container.visible =
                false;

            return {
                rosterIndex:
                    discovered.rosterIndex,

                team:
                    discovered.team,

                playerPanel:
                    discovered.playerPanel,

                kdaContainer:
                    kdaContainer,

                container:
                    container,

                valueLabel:
                    valueLabel,

                heroDamage:
                    null,

                damageColor:
                    DAMAGE_COLOR_WHITE
            };
        };

    CurrentMatchHeroDamageOverlay.prototype._calculateDamageColors =
        function () {
            var colors =
                [];

            for (
                var index = 0;
                index < EXPECTED_PLAYERS;
                index += 1
            ) {
                colors.push(
                    DAMAGE_COLOR_WHITE
                );
            }

            this._calculateTeamDamageColors(
                TEAM_ALLY,
                colors
            );

            this._calculateTeamDamageColors(
                TEAM_ENEMY,
                colors
            );

            return colors;
        };

    CurrentMatchHeroDamageOverlay.prototype._calculateTeamDamageColors =
        function (
            team,
            colors
        ) {
            var indexes =
                [];

            var totalDamage =
                0;

            var minimumDamage =
                0;

            var maximumDamage =
                0;

            for (
                var index = 0;
                index < this._bindings.length;
                index += 1
            ) {
                var binding =
                    this._bindings[index];

                if (
                    !binding ||
                    binding.team !== team ||
                    !this._isValidDamage(
                        binding.heroDamage
                    )
                ) {
                    continue;
                }

                var damage =
                    binding.heroDamage;

                if (indexes.length === 0) {
                    minimumDamage =
                        damage;

                    maximumDamage =
                        damage;
                } else {
                    minimumDamage =
                        Math.min(
                            minimumDamage,
                            damage
                        );

                    maximumDamage =
                        Math.max(
                            maximumDamage,
                            damage
                        );
                }

                totalDamage +=
                    damage;

                indexes.push(
                    index
                );
            }

            /*
             * The desktop CURRENT HERO STATS rules deliberately keep
             * a one-player comparison and an all-equal team white.
             */
            if (
                indexes.length < 2 ||
                minimumDamage === maximumDamage
            ) {
                return;
            }

            for (
                var teamIndex = 0;
                teamIndex < indexes.length;
                teamIndex += 1
            ) {
                var rosterIndex =
                    indexes[teamIndex];

                var heroDamage =
                    this._bindings[
                        rosterIndex
                    ].heroDamage;

                /*
                 * heroDamage >= average * 1.20
                 *
                 * Multiplying both sides keeps the exact inclusive
                 * boundary and avoids floating-point division:
                 *
                 * heroDamage * validCount * 5 >= totalDamage * 6
                 */
                if (
                    heroDamage *
                        indexes.length *
                        5 >=
                    totalDamage * 6
                ) {
                    colors[rosterIndex] =
                        DAMAGE_COLOR_PURPLE;
                } else if (
                    heroDamage ===
                        maximumDamage
                ) {
                    colors[rosterIndex] =
                        DAMAGE_COLOR_GREEN;
                } else if (
                    heroDamage ===
                        minimumDamage
                ) {
                    colors[rosterIndex] =
                        DAMAGE_COLOR_RED;
                }
            }
        };

    CurrentMatchHeroDamageOverlay.prototype._isValidDamage =
        function (
            value
        ) {
            return (
                typeof value === 'number' &&
                isFinite(value) &&
                value >= 0 &&
                Math.floor(value) === value
            );
        };

    CurrentMatchHeroDamageOverlay.prototype._getOrCreateLabel =
        function (
            parent,
            id
        ) {
            var label =
                parent.FindChild(
                    id
                );

            if (
                !isValidPanel(
                    label
                )
            ) {
                label =
                    $.CreatePanel(
                        'Label',
                        parent,
                        id
                    );
            }

            return isValidPanel(
                label
            )
                ? label
                : null;
        };

    CurrentMatchHeroDamageOverlay.prototype._configureContainer =
        function (
            container
        ) {
            container.hittest =
                false;

            container.hittestchildren =
                false;

            container.style.width =
                'fit-children';

            container.style.height =
                '13px';

            container.style.flowChildren =
                'right';

            container.style.horizontalAlign =
                'center';

            container.style.verticalAlign =
                'top';
        };

    CurrentMatchHeroDamageOverlay.prototype._configureCaptionLabel =
        function (
            label
        ) {
            this._configureLabel(
                label
            );

            label.style.color =
                '#C9CED2';
        };

    CurrentMatchHeroDamageOverlay.prototype._configureValueLabel =
        function (
            label
        ) {
            this._configureLabel(
                label
            );

            label.style.marginLeft =
                '2px';

            label.style.fontWeight =
                'bold';

            label.style.color =
                DAMAGE_COLOR_WHITE;
        };

    CurrentMatchHeroDamageOverlay.prototype._configureLabel =
        function (
            label
        ) {
            label.hittest =
                false;

            label.style.width =
                'fit-children';

            label.style.height =
                '13px';

            label.style.fontSize =
                '9px';

            label.style.verticalAlign =
                'center';

            label.style.whiteSpace =
                'nowrap';

            label.style.textShadow =
                '1px 1px 1px 1.0 #000000';
        };

    ThreatHud.CurrentMatchHeroDamageOverlay =
        CurrentMatchHeroDamageOverlay;

})(ThreatHud);
