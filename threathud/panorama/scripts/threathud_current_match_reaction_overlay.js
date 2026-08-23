var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS = 12;

    var NORMAL_CONTROLS_ID = 'ThreatHudReactionControls';
    var SPECTATE_CONTROLS_ID = 'ThreatHudReactionSpectateControls';
    var SPECTATE_PANEL_ID = 'SpectatePlayerButtonPanel';
    var SCOREBOARD_VISIBILITY_CLASS = 'KillsAndAbilityPointsContainer';
    var BUTTON_ICON_ID = 'ThreatHudReactionButtonIcon';

    var NORMAL_BUTTON_SIZE = 18;
    var SPECTATE_HITBOX_SIZE = 24;

    var ReactionValue = ThreatHud.PlayerReactionValue;
    var ReactionVisuals = ThreatHud.ReactionVisuals;
    var UiUtils = ThreatHud.CurrentMatchUiUtils;
    var isValidPanel = UiUtils.isValidPanel;

    var BUTTONS = [
        {
            reaction: ReactionValue.dislike,
            horizontalAlign: 'left',
            normalId: 'ThreatHudReactionDislikeButton',
            spectateId: 'ThreatHudReactionSpectateDislikeButton'
        },
        {
            reaction: ReactionValue.like,
            horizontalAlign: 'right',
            normalId: 'ThreatHudReactionLikeButton',
            spectateId: 'ThreatHudReactionSpectateLikeButton'
        }
    ];

    function CurrentMatchReactionOverlay(topPanel, logger) {
        this._topPanel = topPanel;
        this._log = typeof logger === 'function' ? logger : function () {};
        this._renderedPanels = [];
    }

    CurrentMatchReactionOverlay.prototype.render = function (
        players,
        onReactionRequested
    ) {
        if (typeof onReactionRequested !== 'function') {
            this._log('Reaction overlay: invalid callback');
            return false;
        }

        if (!UiUtils.validatePlayers(
            players,
            EXPECTED_PLAYERS,
            this._log,
            'Reaction overlay'
        )) {
            return false;
        }

        this.clear();

        var handledCount = 0;
        var visibleCount = 0;

        for (var index = 0; index < players.length; index += 1) {
            var player = players[index];
            var result = this._renderPlayer(player, onReactionRequested);

            if (!result.handled) {
                continue;
            }

            handledCount += 1;
            visibleCount += result.visible ? 1 : 0;
            this._rememberPanel(player.panel);
        }

        this._log(
            'Reaction overlay: render complete' +
                ' | handled=' + handledCount + '/' + players.length +
                ' | visible=' + visibleCount
        );

        return handledCount === players.length;
    };

    CurrentMatchReactionOverlay.prototype.clear = function () {
        var hiddenCount = 0;

        for (var index = 0; index < this._renderedPanels.length; index += 1) {
            if (this._hideControls(this._renderedPanels[index])) {
                hiddenCount += 1;
            }
        }

        this._renderedPanels = [];

        if (hiddenCount > 0) {
            this._log('Reaction overlay: CLEAR | hidden=' + hiddenCount);
        }

        return hiddenCount;
    };

    CurrentMatchReactionOverlay.prototype.updateReaction = function (
        player,
        reaction
    ) {
        if (
            !UiUtils.isBindingCurrent(player, this._log, 'Reaction overlay') ||
            !ReactionValue.isValid(reaction)
        ) {
            return false;
        }

        var controls = this._findNormalControls(player.panel);

        return isValidPanel(controls) &&
            this._applyReactionState(controls, reaction);
    };

    CurrentMatchReactionOverlay.prototype.setPending = function (
        player,
        pending
    ) {
        if (!player || !isValidPanel(player.panel)) {
            return false;
        }

        var normalControls = this._findNormalControls(player.panel);

        if (
            !isValidPanel(normalControls) ||
            !this._setButtonsPending(normalControls, pending, false)
        ) {
            return false;
        }

        var spectateControls = this._findSpectateControls(player.panel);

        if (isValidPanel(spectateControls)) {
            this._setButtonsPending(spectateControls, pending, true);
        }

        return true;
    };

    CurrentMatchReactionOverlay.prototype._renderPlayer = function (
        player,
        onReactionRequested
    ) {
        var playerPanel = player.panel;

        if (!isValidPanel(playerPanel)) {
            return this._renderResult(false, false);
        }

        var playerNameNWContainer = playerPanel.FindChildTraverse(
            'PlayerNameNWContainer'
        );

        if (!isValidPanel(playerNameNWContainer)) {
            this._log(
                'Reaction overlay: PlayerNameNWContainer not found' +
                    ' | rosterIndex=' + player.rosterIndex
            );
            return this._renderResult(false, false);
        }

        var backgroundStrip = playerNameNWContainer.FindChild(
            'BackgroundStrip'
        );

        if (!isValidPanel(backgroundStrip)) {
            this._log(
                'Reaction overlay: BackgroundStrip not found' +
                    ' | rosterIndex=' + player.rosterIndex
            );
            return this._renderResult(false, false);
        }

        if (!this._isValidUInt32Key(player.accountId)) {
            this._hideControls(playerPanel);
            return this._renderResult(true, false);
        }

        if (!ReactionValue.isValid(player.reaction)) {
            this._log(
                'Reaction overlay: invalid reaction' +
                    ' | rosterIndex=' + player.rosterIndex +
                    ' | reaction=' + String(player.reaction)
            );
            return this._renderResult(false, false);
        }

        this._configureNormalInteraction(
            playerPanel,
            playerNameNWContainer,
            backgroundStrip
        );

        var normalControls = this._getOrCreateControls(
            backgroundStrip,
            NORMAL_CONTROLS_ID
        );

        if (!isValidPanel(normalControls)) {
            return this._renderResult(false, false);
        }

        this._configureNormalControls(normalControls);

        if (
            !this._buildButtons(
                normalControls,
                player,
                onReactionRequested,
                false
            ) ||
            !this._applyReactionState(normalControls, player.reaction)
        ) {
            return this._renderResult(false, false);
        }

        normalControls.visible = true;
        normalControls.enabled = true;
        normalControls.hittest = true;
        normalControls.hittestchildren = true;

        /*
         * Input-only duplicates live inside the native spectator panel.
         * Failure here must not break the normal reaction overlay.
         */
        this._renderSpectateControls(
            playerPanel,
            player,
            onReactionRequested
        );

        return this._renderResult(true, true);
    };

    CurrentMatchReactionOverlay.prototype._renderSpectateControls = function (
        playerPanel,
        player,
        onReactionRequested
    ) {
        var spectatePanel = playerPanel.FindChildTraverse(SPECTATE_PANEL_ID);

        if (!isValidPanel(spectatePanel)) {
            return false;
        }

        try {
            spectatePanel.hittestchildren = true;
            spectatePanel.style.overflow = 'noclip';
        } catch (interactionError) {
        }

        var controls = this._getOrCreateControls(
            spectatePanel,
            SPECTATE_CONTROLS_ID
        );

        if (!isValidPanel(controls)) {
            return false;
        }

        /* Native class supplies scoreboard-only visibility. */
        controls.AddClass(SCOREBOARD_VISIBILITY_CLASS);
        this._configureSpectateControls(controls);

        if (!this._buildButtons(
            controls,
            player,
            onReactionRequested,
            true
        )) {
            return false;
        }

        controls.enabled = true;
        controls.hittest = false;
        controls.hittestchildren = true;
        controls.style.opacity = '1.0';

        this._promoteInput(controls);
        return true;
    };

    CurrentMatchReactionOverlay.prototype._getOrCreateControls = function (
        parent,
        id
    ) {
        var controls = parent.FindChild(id);

        if (!isValidPanel(controls)) {
            controls = $.CreatePanel('Panel', parent, id);
        }

        return isValidPanel(controls) ? controls : null;
    };

    CurrentMatchReactionOverlay.prototype._buildButtons = function (
        controls,
        player,
        onReactionRequested,
        spectateOnly
    ) {
        for (var index = 0; index < BUTTONS.length; index += 1) {
            var descriptor = BUTTONS[index];
            var button = this._getOrCreateButton(
                controls,
                descriptor,
                spectateOnly
            );

            if (!isValidPanel(button)) {
                return false;
            }

            this._bindButton(
                button,
                player,
                descriptor.reaction,
                onReactionRequested
            );
        }

        return true;
    };

    CurrentMatchReactionOverlay.prototype._getOrCreateButton = function (
        parent,
        descriptor,
        spectateOnly
    ) {
        var id = spectateOnly ? descriptor.spectateId : descriptor.normalId;
        var button = parent.FindChild(id);

        if (!isValidPanel(button)) {
            button = $.CreatePanel('Button', parent, id);
        }

        if (!isValidPanel(button)) {
            return null;
        }

        this._configureButton(
            button,
            descriptor.horizontalAlign,
            spectateOnly
        );

        if (spectateOnly) {
            this._hideLegacySpectateIcon(button);
            this._promoteInput(button);
            return button;
        }

        var icon = ReactionVisuals.getOrCreateImage(
            button,
            BUTTON_ICON_ID,
            descriptor.reaction,
            12
        );

        return isValidPanel(icon) ? button : null;
    };

    CurrentMatchReactionOverlay.prototype._configureButton = function (
        button,
        horizontalAlign,
        spectateOnly
    ) {
        var size = spectateOnly
            ? SPECTATE_HITBOX_SIZE
            : NORMAL_BUTTON_SIZE;

        button.hittest = true;
        button.hittestchildren = !spectateOnly;
        button.enabled = true;

        button.style.width = String(size) + 'px';
        button.style.height = String(size) + 'px';
        button.style.horizontalAlign = horizontalAlign;
        button.style.verticalAlign = 'center';
        button.style.zIndex = spectateOnly ? '6001' : '1201';

        if (spectateOnly) {
            button.style.ignoreParentFlow = 'true';
        }
    };

    CurrentMatchReactionOverlay.prototype._bindButton = function (
        button,
        player,
        requestedReaction,
        onReactionRequested
    ) {
        button.SetPanelEvent('onactivate', function () {
            onReactionRequested(player, requestedReaction);
        });
    };

    CurrentMatchReactionOverlay.prototype._configureNormalControls = function (
        controls
    ) {
        controls.visible = false;
        controls.enabled = false;
        controls.hittest = true;
        controls.hittestchildren = true;

        controls.style.width = '100%';
        controls.style.height = '20px';
        controls.style.horizontalAlign = 'center';
        controls.style.verticalAlign = 'top';
        controls.style.position = '0px 90px 0px';
        controls.style.zIndex = '1200';
    };

    CurrentMatchReactionOverlay.prototype._configureSpectateControls = function (
        controls
    ) {
        /* Geometry is inline; the native class is used only for visibility. */
        controls.style.ignoreParentFlow = 'true';
        controls.style.width = '100%';
        controls.style.height = '20px';
        controls.style.marginTop = '0px';
        controls.style.marginBottom = '0px';
        controls.style.horizontalAlign = 'center';
        controls.style.verticalAlign = 'top';
        controls.style.position = '0px 90px 0px';
        controls.style.zIndex = '6000';
        controls.style.overflow = 'noclip';

        controls.enabled = true;
        controls.hittest = false;
        controls.hittestchildren = true;
    };

    CurrentMatchReactionOverlay.prototype._configureNormalInteraction = function (
        playerPanel,
        playerNameNWContainer,
        backgroundStrip
    ) {
        try {
            playerPanel.hittestchildren = true;
            playerNameNWContainer.hittestchildren = true;
            backgroundStrip.hittestchildren = true;

            playerPanel.style.overflow = 'noclip';
            playerNameNWContainer.style.overflow = 'noclip';
            backgroundStrip.style.overflow = 'noclip';
        } catch (interactionError) {
        }
    };

    CurrentMatchReactionOverlay.prototype._applyReactionState = function (
        controls,
        reaction
    ) {
        if (!ReactionValue.isValid(reaction)) {
            return false;
        }

        for (var index = 0; index < BUTTONS.length; index += 1) {
            var descriptor = BUTTONS[index];
            var button = controls.FindChild(descriptor.normalId);

            if (!isValidPanel(button)) {
                return false;
            }

            var icon = button.FindChild(BUTTON_ICON_ID);

            if (!ReactionVisuals.applyButtonState(
                button,
                icon,
                descriptor.reaction,
                reaction
            )) {
                return false;
            }
        }

        return true;
    };

    CurrentMatchReactionOverlay.prototype._setButtonsPending = function (
        controls,
        pending,
        spectateOnly
    ) {
        for (var index = 0; index < BUTTONS.length; index += 1) {
            var descriptor = BUTTONS[index];
            var id = spectateOnly
                ? descriptor.spectateId
                : descriptor.normalId;
            var button = controls.FindChild(id);

            if (!isValidPanel(button)) {
                return false;
            }

            button.enabled = !pending;

            if (!spectateOnly) {
                button.style.opacity = pending ? '0.45' : '1.0';
            }
        }

        return true;
    };

    CurrentMatchReactionOverlay.prototype._hideLegacySpectateIcon = function (
        button
    ) {
        var icon = button.FindChild(BUTTON_ICON_ID);

        if (!isValidPanel(icon)) {
            return;
        }

        icon.visible = false;
        icon.hittest = false;
        icon.style.opacity = '0';
    };

    CurrentMatchReactionOverlay.prototype._promoteInput = function (panel) {
        try {
            panel.SetTopOfInputContext();
        } catch (inputContextError) {
        }
    };

    CurrentMatchReactionOverlay.prototype._findNormalControls = function (
        playerPanel
    ) {
        if (!isValidPanel(playerPanel)) {
            return null;
        }

        var playerNameNWContainer = playerPanel.FindChildTraverse(
            'PlayerNameNWContainer'
        );

        if (!isValidPanel(playerNameNWContainer)) {
            return null;
        }

        var backgroundStrip = playerNameNWContainer.FindChild(
            'BackgroundStrip'
        );

        if (!isValidPanel(backgroundStrip)) {
            return null;
        }

        return backgroundStrip.FindChild(NORMAL_CONTROLS_ID);
    };

    CurrentMatchReactionOverlay.prototype._findSpectateControls = function (
        playerPanel
    ) {
        if (!isValidPanel(playerPanel)) {
            return null;
        }

        var spectatePanel = playerPanel.FindChildTraverse(SPECTATE_PANEL_ID);

        if (!isValidPanel(spectatePanel)) {
            return null;
        }

        return spectatePanel.FindChild(SPECTATE_CONTROLS_ID);
    };

    /* Kept for compatibility with the previous overlay implementation. */
    CurrentMatchReactionOverlay.prototype._findControls = function (
        playerPanel
    ) {
        return this._findNormalControls(playerPanel);
    };

    CurrentMatchReactionOverlay.prototype._hideControls = function (
        playerPanel
    ) {
        var changed = false;
        var normalControls = this._findNormalControls(playerPanel);

        if (isValidPanel(normalControls)) {
            changed = normalControls.visible || changed;
            normalControls.visible = false;
            normalControls.enabled = false;
            normalControls.hittest = false;
            normalControls.hittestchildren = false;
        }

        var spectateControls = this._findSpectateControls(playerPanel);

        if (isValidPanel(spectateControls)) {
            changed = true;
            spectateControls.style.opacity = '0';
            spectateControls.enabled = false;
            spectateControls.hittest = false;
            spectateControls.hittestchildren = false;
        }

        return changed;
    };

    CurrentMatchReactionOverlay.prototype._rememberPanel = function (panel) {
        if (
            !isValidPanel(panel) ||
            UiUtils.containsPanel(this._renderedPanels, panel)
        ) {
            return;
        }

        this._renderedPanels.push(panel);
    };

    CurrentMatchReactionOverlay.prototype._renderResult = function (
        handled,
        visible
    ) {
        return {
            handled: handled,
            visible: visible
        };
    };

    CurrentMatchReactionOverlay.prototype._isValidUInt32Key = function (value) {
        var numeric = Number(value);

        return (
            isFinite(numeric) &&
            numeric >= 1 &&
            numeric <= 4294967295 &&
            Math.floor(numeric) === numeric
        );
    };

    ThreatHud.CurrentMatchReactionOverlay = CurrentMatchReactionOverlay;

})(ThreatHud);