var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var MESSAGE_PLAYER_REACTION_ACK =
		5;

	var ACK_PAYLOAD_BYTES =
		5;

	var UINT32_MAX =
		4294967295;

	var PlayerReactionValue = {
		dislike:
			-1,

		none:
			0,

		like:
			1,

		isValid:
			function (reaction) {
				return (
					reaction === -1 ||
					reaction === 0 ||
					reaction === 1
				);
			}
	};

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
			'Invalid reaction ACK: ' +
				value
		);
	}

	function PlayerReactionClient(
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

		/*
		 * For one accountId, no more than one
		 * write is performed at a time.
		 */
		this._pendingAccountIds =
			{};
	}

	PlayerReactionClient.prototype.setReaction =
		function (
			accountId,
			reaction,
			callback
		) {
			var normalizedAccountId =
				this._normalizeUInt32(
					accountId
				);

			if (
				normalizedAccountId === null ||
				normalizedAccountId === 0
			) {
				this._invokeCallback(
					callback,

					this._createError(
						'invalid-account-id',
						'accountId must be a non-zero uint32.',
						accountId
					),

					null
				);

				return false;
			}

			var normalizedReaction =
				Number(
					reaction
				);

			if (
				!PlayerReactionValue.isValid(
					normalizedReaction
				)
			) {
				this._invokeCallback(
					callback,

					this._createError(
						'invalid-reaction',
						'Reaction must be -1, 0, or 1.',
						reaction
					),

					null
				);

				return false;
			}

			var requestKey =
				String(
					normalizedAccountId
				);

			if (
				this._pendingAccountIds
					.hasOwnProperty(
						requestKey
					)
			) {
				this._invokeCallback(
					callback,

					this._createError(
						'already-running',
						'This player reaction is already being written.',
						requestKey
					),

					null
				);

				return false;
			}

			this._pendingAccountIds[
				requestKey
			] =
				true;

			this._log(
				'PlayerReactionClient: REQUEST' +
					' | accountId=' +
					normalizedAccountId +
					' | reaction=' +
					normalizedReaction
			);

			var self =
				this;

			var callbackInvoked =
				false;

			var started =
				this._transport.requestPacket(
					'player-reaction-set',

					{
						accountId:
							String(
								normalizedAccountId
							),

						reaction:
							String(
								normalizedReaction
							)
					},

					function (
						error,
						packet
					) {
						callbackInvoked =
							true;

						delete self
							._pendingAccountIds[
								requestKey
							];

						if (error) {
							self._invokeCallback(
								callback,
								error,
								null
							);

							return;
						}

						if (
							!packet ||
							packet.messageType !==
								MESSAGE_PLAYER_REACTION_ACK
						) {
							self._invokeCallback(
								callback,

								self._createError(
									'unexpected-message-type',
									'Bridge returned an unexpected reaction ACK type.',
									packet
										? packet.messageType
										: null
								),

								null
							);

							return;
						}

						try {
							var result =
								self._decodePayload(
									packet.payload,
									normalizedAccountId,
									normalizedReaction
								);

							result.session =
								packet.session;

							self._log(
								'PlayerReactionClient: RESPONSE' +
									' | accountId=' +
									result.accountId +
									' | reaction=' +
									result.reaction
							);

							self._invokeCallback(
								callback,
								null,
								result
							);
						} catch (
							decodeError
						) {
							self._invokeCallback(
								callback,

								self._createError(
									'invalid-reaction-ack-payload',
									'Failed to parse reaction ACK payload.',
									String(
										decodeError
									)
								),

								null
							);
						}
					}
				);

			if (
				!started &&
				!callbackInvoked
			) {
				delete this
					._pendingAccountIds[
						requestKey
					];

				this._invokeCallback(
					callback,

					this._createError(
						'client-not-started',
						'Reaction transport did not start the request.',
						null
					),

					null
				);
			}

			return started;
		};

	PlayerReactionClient.prototype._decodePayload =
		function (
			payload,
			requestedAccountId,
			requestedReaction
		) {
			if (
				!payload ||
				payload.length !==
					ACK_PAYLOAD_BYTES
			) {
				throw new Error(
					'Invalid reaction ACK payload size' +
						' | expected=' +
						ACK_PAYLOAD_BYTES +
						' | actual=' +
						(
							payload
								? payload.length
								: 0
						)
				);
			}

			var accountId =
				this._readUInt32LittleEndian(
					payload,
					0
				);

			var reaction =
				decodeReaction(
					payload[4]
				);

			if (
				accountId !==
					requestedAccountId
			) {
				throw new Error(
					'Reaction ACK accountId does not match' +
						' | requested=' +
						requestedAccountId +
						' | received=' +
						accountId
				);
			}

			if (
				reaction !==
					requestedReaction
			) {
				throw new Error(
					'Reaction ACK does not match the requested value' +
						' | requested=' +
						requestedReaction +
						' | stored=' +
						reaction
				);
			}

			return {
				accountId:
					accountId,

				accountIdText:
					String(
						accountId
					),

				reaction:
					reaction
			};
		};

	PlayerReactionClient.prototype._readUInt32LittleEndian =
		function (
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
		};

	PlayerReactionClient.prototype._normalizeUInt32 =
		function (value) {
			var numeric =
				Number(
					value
				);

			if (
				!isFinite(
					numeric
				) ||
				numeric < 0 ||
				numeric > UINT32_MAX ||
				Math.floor(
					numeric
				) !== numeric
			) {
				return null;
			}

			return numeric;
		};

	PlayerReactionClient.prototype._createError =
		function (
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
					detail === undefined
						? null
						: detail
			};
		};

	PlayerReactionClient.prototype._invokeCallback =
		function (
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
		};

	ThreatHud.PlayerReactionValue =
		PlayerReactionValue;

	ThreatHud.PlayerReactionClient =
		PlayerReactionClient;

})(ThreatHud);