(function () {
    'use strict';

    var LABEL_ID =
        'ClientServerDebugStats';

    var BEACON_ID =
        'ThreatHudMatchContextBeacon';

    var BRIDGE_URL =
        'http://127.0.0.1:28741/current-match-context.png';

    var POLL_INTERVAL =
        1.0;

    /*
     * The heartbeat lets a restarted Bridge
     * recover the current match ID without
     * waiting for a new match.
     */
    var HEARTBEAT_INTERVAL_MS =
        30000;

    /*
     * A single missing MATCH line can occur while Panorama refreshes the
     * debug label. Confirm the clear before ending the Bridge match epoch;
     * otherwise the same match would request /live/url again when it returns.
     */
    var CLEAR_CONFIRMATION_POLLS =
        3;

    var lastObservedMatchId =
        '0';

    var lastSentAtMs =
        0;

    var consecutiveClearPolls =
        0;

    function getLabel() {
        var panel =
            $(
                '#' +
                LABEL_ID
            );

        if (
            !panel ||
            !panel.IsValid()
        ) {
            return null;
        }

        return panel;
    }

    function getBeacon(
        label
    ) {
        var panel =
            $(
                '#' +
                BEACON_ID
            );

        if (
            panel &&
            panel.IsValid()
        ) {
            return panel;
        }

        var parent =
            label.GetParent();

        if (!parent)
        {
            return null;
        }

        panel =
            $.CreatePanel(
                'Image',
                parent,
                BEACON_ID
            );

        panel.hittest =
            false;

        panel.style.width =
            '1px';

        panel.style.height =
            '1px';

        panel.style.opacity =
            '0.0';

        panel.style.position =
            '0px 0px 0px';

        return panel;
    }

    function parseMatchId(
        text
    ) {
        var match =
            /:\s*(\d+)/.exec(
                text
            );

        if (
            !match ||
            !match[1]
        ) {
            return '0';
        }

        return String(
            match[1]
        );
    }

    function sendMatchId(
        label,
        matchId
    ) {
        var beacon =
            getBeacon(
                label
            );

        if (!beacon)
        {
            return false;
        }

        var url =
            BRIDGE_URL +
            '?matchId=' +
            matchId +
            '&_=' +
            String(
                Date.now()
            );

        try {
            beacon.SetImage(
                url
            );
        } catch (error) {
            $.Msg(
                '[MATCH-CONTEXT] send failed: ' +
                error
            );

            return false;
        }

        lastSentAtMs =
            Date.now();

        return true;
    }

    function poll() {
        var label =
            getLabel();

        if (!label)
        {
            $.Schedule(
                POLL_INTERVAL,
                poll
            );

            return;
        }

        var text =
            '';

        try {
            text =
                String(
                    label.text ||
                    ''
                );
        } catch (error) {
            $.Schedule(
                POLL_INTERVAL,
                poll
            );

            return;
        }

        var matchId =
            parseMatchId(
                text
            );

        if (
            matchId === '0' &&
            lastObservedMatchId !== '0'
        ) {
            consecutiveClearPolls +=
                1;

            if (
                consecutiveClearPolls <
                    CLEAR_CONFIRMATION_POLLS
            ) {
                $.Schedule(
                    POLL_INTERVAL,
                    poll
                );

                return;
            }
        } else {
            consecutiveClearPolls =
                0;
        }

        var now =
            Date.now();

        var changed =
            matchId !==
                lastObservedMatchId;

        var heartbeatDue =
            matchId !==
                '0' &&
            now -
                lastSentAtMs >=
                HEARTBEAT_INTERVAL_MS;

        if (changed)
        {
            lastObservedMatchId =
                matchId;

            if (matchId === '0')
            {
                $.Msg(
                    '[MATCH-CONTEXT] cleared'
                );
            }
            else
            {
                $.Msg(
                    '[MATCH-CONTEXT] MATCH_ID=' +
                    matchId
                );
            }

            sendMatchId(
                label,
                matchId
            );
        }
        else if (heartbeatDue)
        {
            sendMatchId(
                label,
                matchId
            );
        }

        $.Schedule(
            POLL_INTERVAL,
            poll
        );
    }

    $.Schedule(
        0.0,
        poll
    );
})();
