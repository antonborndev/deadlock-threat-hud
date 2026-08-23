var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
    'use strict';

    var EXPECTED_PLAYERS =
        12;

    var MESSAGE_HERO_DAMAGE =
        7;

    var PAYLOAD_HEADER_SIZE =
        10;

    var BYTES_PER_PLAYER =
        5;

    var STATE_NO_MATCH =
        0;

    var STATE_WAITING =
        1;

    var STATE_SNAPSHOT =
        2;

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

    function createError(
        code,
        message,
        detail
    ) {
        return {
            code:
                String(
                    code ||
                        'unknown-error'
                ),

            message:
                String(
                    message ||
                        ''
                ),

            detail:
                detail ===
                    undefined
                        ? null
                        : detail
        };
    }

    function invokeCallback(
        callback,
        error,
        result
    ) {
        if (
            typeof callback ===
                'function'
        ) {
            callback(
                error || null,
                result || null
            );
        }
    }

    function getStateName(
        stateCode
    ) {
        if (
            stateCode ===
                STATE_NO_MATCH
        ) {
            return 'no-match';
        }

        if (
            stateCode ===
                STATE_WAITING
        ) {
            return 'waiting';
        }

        if (
            stateCode ===
                STATE_SNAPSHOT
        ) {
            return 'snapshot';
        }

        throw new Error(
            'Unknown hero-damage state: ' +
                stateCode
        );
    }

    function getAccountIdText(
        match
    ) {
        if (
            !match ||
            match.status !==
                'resolved'
        ) {
            return '0';
        }

        var accountIdText =
            String(
                match.accountIdText ||
                    match.accountId ||
                    ''
            );

        if (
            !/^\d+$/.test(
                accountIdText
            ) ||
            accountIdText ===
                '0'
        ) {
            throw new Error(
                'Resolved hero-damage player has an invalid account ID.' +
                    ' | rosterIndex=' +
                    String(
                        match.rosterIndex
                    )
            );
        }

        var numericAccountId =
            Number(
                accountIdText
            );

        if (
            !isFinite(
                numericAccountId
            ) ||
            numericAccountId < 1 ||
            numericAccountId >
                4294967295 ||
            Math.floor(
                numericAccountId
            ) !==
                numericAccountId
        ) {
            throw new Error(
                'Resolved hero-damage account ID is outside uint32.' +
                    ' | rosterIndex=' +
                    String(
                        match.rosterIndex
                    )
            );
        }

        return accountIdText;
    }

    function prepareRequest(
        matches
    ) {
        if (
            !matches ||
            matches.length !==
                EXPECTED_PLAYERS
        ) {
            throw new Error(
                'Hero-damage request requires exactly ' +
                    EXPECTED_PLAYERS +
                    ' roster entries.'
            );
        }

        var requestedPlayers =
            [];

        var usedAccountIds =
            [];

        var parameters = {
            count:
                EXPECTED_PLAYERS
        };

        for (
            var index = 0;
            index < EXPECTED_PLAYERS;
            index += 1
        ) {
            var match =
                matches[index];

            if (
                !match ||
                match.rosterIndex !==
                    index
            ) {
                throw new Error(
                    'Hero-damage roster index mismatch.' +
                        ' | expected=' +
                        index +
                        ' | actual=' +
                        String(
                            match
                                ? match.rosterIndex
                                : -1
                        )
                );
            }

            var accountIdText =
                getAccountIdText(
                    match
                );

            if (
                accountIdText !==
                    '0'
            ) {
                for (
                    var usedIndex = 0;
                    usedIndex <
                        usedAccountIds.length;
                    usedIndex += 1
                ) {
                    if (
                        usedAccountIds[
                            usedIndex
                        ] ===
                            accountIdText
                    ) {
                        throw new Error(
                            'Duplicate hero-damage account ID.' +
                                ' | rosterIndex=' +
                                index +
                                ' | accountId=' +
                                accountIdText
                        );
                    }
                }

                usedAccountIds.push(
                    accountIdText
                );
            }

            parameters[
                'a' + index
            ] =
                accountIdText;

            requestedPlayers.push({
                rosterIndex:
                    index,

                accountIdText:
                    accountIdText
            });
        }

        return {
            parameters:
                parameters,

            players:
                requestedPlayers
        };
    }

    function decodePayload(
        payload,
        requestedPlayers
    ) {
        if (
            !payload ||
            payload.length <
                PAYLOAD_HEADER_SIZE
        ) {
            throw new Error(
                'Hero-damage payload is truncated.'
            );
        }

        var stateCode =
            payload[0];

        var state =
            getStateName(
                stateCode
            );

        var matchIdLow =
            readUInt32LittleEndian(
                payload,
                1
            );

        var matchIdHigh =
            readUInt32LittleEndian(
                payload,
                5
            );

        var playerCount =
            payload[9];

        if (
            playerCount !==
                EXPECTED_PLAYERS ||
            playerCount !==
                requestedPlayers.length
        ) {
            throw new Error(
                'Invalid hero-damage player count.' +
                    ' | expected=' +
                    EXPECTED_PLAYERS +
                    ' | actual=' +
                    playerCount
            );
        }

        var expectedLength =
            PAYLOAD_HEADER_SIZE +
            playerCount *
                BYTES_PER_PLAYER;

        if (
            payload.length !==
                expectedLength
        ) {
            throw new Error(
                'Invalid hero-damage payload size.' +
                    ' | expected=' +
                    expectedLength +
                    ' | actual=' +
                    payload.length
            );
        }

        if (
            stateCode ===
                STATE_NO_MATCH
        ) {
            if (
                matchIdLow !== 0 ||
                matchIdHigh !== 0
            ) {
                throw new Error(
                    'No-match hero-damage payload contains a match ID.'
                );
            }
        } else if (
            matchIdLow === 0 &&
            matchIdHigh === 0
        ) {
            throw new Error(
                'Hero-damage payload has no match ID.'
            );
        }

        var players =
            [];

        var presentCount =
            0;

        for (
            var index = 0;
            index < playerCount;
            index += 1
        ) {
            var offset =
                PAYLOAD_HEADER_SIZE +
                index *
                    BYTES_PER_PLAYER;

            var presentValue =
                payload[offset];

            if (
                presentValue !== 0 &&
                presentValue !== 1
            ) {
                throw new Error(
                    'Invalid hero-damage present flag.' +
                        ' | rosterIndex=' +
                        index +
                        ' | value=' +
                        presentValue
                );
            }

            var damage =
                readUInt32LittleEndian(
                    payload,
                    offset + 1
                );

            if (
                presentValue === 0 &&
                damage !== 0
            ) {
                throw new Error(
                    'Absent hero-damage entry contains a value.' +
                        ' | rosterIndex=' +
                        index
                );
            }

            if (
                stateCode !==
                    STATE_SNAPSHOT &&
                presentValue !== 0
            ) {
                throw new Error(
                    'Non-snapshot hero-damage payload contains player data.'
                );
            }

            if (
                presentValue === 1
            ) {
                presentCount += 1;
            }

            players.push({
                rosterIndex:
                    requestedPlayers[index]
                        .rosterIndex,

                requestedAccountIdText:
                    requestedPlayers[index]
                        .accountIdText,

                present:
                    presentValue === 1,

                heroDamage:
                    damage
            });
        }

        return {
            stateCode:
                stateCode,

            state:
                state,

            matchIdLow:
                matchIdLow,

            matchIdHigh:
                matchIdHigh,

            matchKey:
                String(
                    matchIdHigh
                ) +
                ':' +
                String(
                    matchIdLow
                ),

            count:
                playerCount,

            presentCount:
                presentCount,

            players:
                players
        };
    }

    function HeroDamageClient(
        localHostClient,
        logger
    ) {
        this._transport =
            localHostClient;

        this._log =
            typeof logger ===
                'function'
                    ? logger
                    : function () {};
    }

    HeroDamageClient.prototype.getForMatches =
        function (
            matches,
            callback
        ) {
            var request;

            try {
                request =
                    prepareRequest(
                        matches
                    );
            } catch (
                requestError
            ) {
                invokeCallback(
                    callback,

                    createError(
                        'invalid-hero-damage-request',
                        'Failed to prepare hero-damage request.',
                        String(
                            requestError
                        )
                    ),

                    null
                );

                return false;
            }

            this._log(
                'HeroDamageClient: REQUEST' +
                    ' | players=' +
                    request.players.length
            );

            var self =
                this;

            return this._transport.requestPacket(
                'current-match-hero-damage',
                request.parameters,

                function (
                    error,
                    packet
                ) {
                    if (error) {
                        invokeCallback(
                            callback,
                            error,
                            null
                        );

                        return;
                    }

                    if (
                        !packet ||
                        packet.messageType !==
                            MESSAGE_HERO_DAMAGE
                    ) {
                        invokeCallback(
                            callback,

                            createError(
                                'unexpected-message-type',
                                'Bridge returned an unexpected hero-damage message type.',
                                packet
                                    ? packet.messageType
                                    : null
                            ),

                            null
                        );

                        return;
                    }

                    var result;

                    try {
                        result =
                            decodePayload(
                                packet.payload,
                                request.players
                            );
                    } catch (
                        decodeError
                    ) {
                        invokeCallback(
                            callback,

                            createError(
                                'invalid-hero-damage-payload',
                                'Failed to parse hero-damage payload.',
                                String(
                                    decodeError
                                )
                            ),

                            null
                        );

                        return;
                    }

                    result.session =
                        packet.session;

                    self._log(
                        'HeroDamageClient: RESPONSE' +
                            ' | state=' +
                            result.state +
                            ' | match=' +
                            result.matchKey +
                            ' | present=' +
                            result.presentCount +
                            '/' +
                            result.count
                    );

                    invokeCallback(
                        callback,
                        null,
                        result
                    );
                }
            );
        };

    ThreatHud.HeroDamageClient =
        HeroDamageClient;

})(ThreatHud);
