var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var ReactionValue =
        ThreatHud.PlayerReactionValue;

    var UiUtils =
        ThreatHud.CurrentMatchUiUtils;

    var isValidPanel =
        UiUtils.isValidPanel;

    var NEUTRAL_COLOR =
        '#e8e8e8ff';

    var INACTIVE_BACKGROUND =
        '#ffffff00';

    var INACTIVE_BORDER =
        '0px solid #ffffff55';

    /*
     * Physical files inside the VPK:
     *
     * reaction_like.vtex_c
     * reaction_dislike.vtex_c
     *
     * But s2r:// accepts the logical name
     * of the resource without the compiled suffix "_c".
     */
    var VISUALS =
        {};

    VISUALS[
        ReactionValue.like
    ] = {
        image:
            's2r://panorama/images/threathud/reaction_like.vtex',

        color:
            'rgb(17, 255, 0)',

        background:
            'rgba(255, 255, 255, 0)',

        border:
            '0px solid rgb(47, 255, 0)'
    };

    VISUALS[
        ReactionValue.dislike
    ] = {
        image:
            's2r://panorama/images/threathud/reaction_dislike.vtex',

        color:
            'rgb(255, 0, 0)',

        background:
            'rgba(255, 255, 255, 0)',

        border:
            '0px solid rgb(255, 0, 0)'
    };

    function getVisual(reaction) {
        return VISUALS[
            reaction
        ] || null;
    }

    /*
     * The only place where the texture resource is assigned
     * to a dynamic Image panel.
     */
    function setImageForReaction(
        image,
        reaction
    ) {
        if (!isValidPanel(image)) {
            return false;
        }

        var visual =
            getVisual(
                reaction
            );

        if (!visual) {
            return false;
        }

        try {
            image.SetImage(
                visual.image
            );
        } catch (imageError) {
            return false;
        }

        return true;
    }

    function getOrCreateImage(
        parent,
        id,
        reaction,
        size
    ) {
        if (!isValidPanel(parent)) {
            return null;
        }

        var image =
            parent.FindChild(
                id
            );

        if (!isValidPanel(image)) {
            image =
                $.CreatePanel(
                    'Image',
                    parent,
                    id
                );
        }

        if (
            !isValidPanel(image) ||
            !setImageForReaction(
                image,
                reaction
            )
        ) {
            return null;
        }

        try {
            image.SetScaling(
                'stretch-to-fit-preserve-aspect'
            );
        } catch (scalingError) {
            return null;
        }

        image.hittest =
            false;

        image.visible =
            true;

        image.style.width =
            String(size) +
            'px';

        image.style.height =
            String(size) +
            'px';

        image.style.horizontalAlign =
            'center';

        image.style.verticalAlign =
            'center';

        image.style.opacity =
            '1.0';

        return image;
    }

    function applyStatusImage(
        image,
        reaction
    ) {
        if (
            !isValidPanel(image) ||
            !ReactionValue.isValid(
                reaction
            )
        ) {
            return false;
        }

        if (
            reaction ===
                ReactionValue.none
        ) {
            image.visible =
                false;

            return true;
        }

        var visual =
            getVisual(
                reaction
            );

        if (
            !visual ||
            !setImageForReaction(
                image,
                reaction
            )
        ) {
            return false;
        }

        image.style.washColor =
            visual.color;

        image.style.opacity =
            '1.0';

        image.visible =
            true;

        return true;
    }

    function applyButtonState(
        button,
        icon,
        buttonReaction,
        currentReaction
    ) {
        if (
            !isValidPanel(button) ||
            !isValidPanel(icon) ||
            !ReactionValue.isValid(
                currentReaction
            )
        ) {
            return false;
        }

        var visual =
            getVisual(
                buttonReaction
            );

        if (
            !visual ||
            !setImageForReaction(
                icon,
                buttonReaction
            )
        ) {
            return false;
        }

        var active =
            currentReaction ===
                buttonReaction;

        button.style.backgroundColor =
            active
                ? visual.background
                : INACTIVE_BACKGROUND;

        button.style.border =
            active
                ? visual.border
                : INACTIVE_BORDER;

        icon.style.washColor =
            active
                ? visual.color
                : NEUTRAL_COLOR;

        icon.style.opacity =
            '1.0';

        icon.visible =
            true;

        return true;
    }

    ThreatHud.ReactionVisuals = {
        getOrCreateImage:
            getOrCreateImage,

        applyStatusImage:
            applyStatusImage,

        applyButtonState:
            applyButtonState
    };

})(ThreatHud);