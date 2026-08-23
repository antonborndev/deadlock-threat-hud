var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var CHANNEL =
		'service-status';

	var MESSAGE_SERVICE_STATUS_ACK =
		8;

	function isSupportedService(
		service
	) {
		return (
			service === 'winrate' ||
			service === 'rank'
		);
	}

	function isSupportedState(
		state
	) {
		return (
			state === 'in-progress' ||
			state === 'completed' ||
			state === 'error'
		);
	}

	function ServiceStatusClient(
		localHostClient,
		logger
	) {
		this._transport =
			localHostClient;

		this._log =
			typeof logger === 'function'
				? logger
				: function () {};
	}

	ServiceStatusClient.prototype.report =
		function (
			service,
			state
		) {
			if (
				!this._transport ||
				typeof this._transport.requestPacket !==
					'function' ||
				!isSupportedService(service) ||
				!isSupportedState(state)
			) {
				return false;
			}

			var self =
				this;

			var callbackInvoked =
				false;

			var started =
				this._transport.requestPacket(
					CHANNEL,

					{
						service:
							service,

						state:
							state
					},

					function (
						error,
						packet
					) {
						callbackInvoked =
							true;

						if (error) {
							self._log(
								'ServiceStatusClient: ERROR' +
								' | service=' + service +
								' | state=' + state +
								' | code=' +
									String(
										error.code ||
										'unknown-error'
									)
							);

							return;
						}

						if (
							!packet ||
							packet.messageType !==
								MESSAGE_SERVICE_STATUS_ACK ||
							!packet.payload ||
							packet.payload.length !== 1
						) {
							self._log(
								'ServiceStatusClient: INVALID ACK' +
								' | service=' + service +
								' | state=' + state
							);

							return;
						}

						self._log(
							'ServiceStatusClient: ACK' +
							' | service=' + service +
							' | state=' + state +
							' | accepted=' +
								String(
									packet.payload[0] === 1
								)
						);
					}
				);

			if (
				!started &&
				!callbackInvoked
			) {
				this._log(
					'ServiceStatusClient: NOT STARTED' +
					' | service=' + service +
					' | state=' + state
				);
			}

			return started;
		};

	ThreatHud.ServiceStatusClient =
		ServiceStatusClient;

})(ThreatHud);
