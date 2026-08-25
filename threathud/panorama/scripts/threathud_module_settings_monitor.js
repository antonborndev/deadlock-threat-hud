var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var CHANNEL =
		'module-settings';

	var MESSAGE_MODULE_SETTINGS =
		9;

	var POLL_INTERVAL =
		5.0;

	var INITIAL_POLL_DELAY =
		0.05;

	var KNOWN_MASK =
		15;

	function ModuleSettingsMonitor(
		localHostClient,
		logger,
		onSettingsChanged
	) {
		this._transport =
			localHostClient;

		this._log =
			typeof logger === 'function'
				? logger
				: function () {};

		this._onSettingsChanged =
			typeof onSettingsChanged ===
				'function'
					? onSettingsChanged
					: function () {};

		this._running =
			false;

		this._requestRunning =
			false;

		this._generation =
			0;

		/*
		 * null means that Bridge has not yet supplied settings.
		 * Presentation modules remain fail-closed until that happens.
		 */
		this._lastMask =
			null;

		this._lastErrorKey =
			null;
	}

	ModuleSettingsMonitor.prototype.start =
		function () {
			if (this._running) {
				return false;
			}

			this._running =
				true;

			this._requestRunning =
				false;

			this._generation +=
				1;

			this._schedule(
				INITIAL_POLL_DELAY,
				this._generation
			);

			return true;
		};

	ModuleSettingsMonitor.prototype.stop =
		function () {
			if (!this._running) {
				return false;
			}

			this._running =
				false;

			this._requestRunning =
				false;

			this._generation +=
				1;

			return true;
		};

	ModuleSettingsMonitor.prototype._schedule =
		function (
			delay,
			generation
		) {
			var self =
				this;

			$.Schedule(
				delay,

				function () {
					if (
						!self._running ||
						generation !==
							self._generation
					) {
						return;
					}

					self._poll(
						generation
					);
				}
			);
		};

	ModuleSettingsMonitor.prototype._poll =
		function (generation) {
			if (
				!this._running ||
				generation !==
					this._generation ||
				this._requestRunning
			) {
				return false;
			}

			if (
				!this._transport ||
				typeof this._transport
					.requestPacket !==
						'function'
			) {
				this._handleError(
					generation,
					'transport-unavailable',
					'Module settings transport is unavailable.'
				);

				return false;
			}

			this._requestRunning =
				true;

			var self =
				this;

			var callbackInvoked =
				false;

			var started =
				this._transport.requestPacket(
					CHANNEL,
					{},

					function (
						error,
						packet
					) {
						callbackInvoked =
							true;

						if (
							!self._running ||
							generation !==
								self._generation
						) {
							return;
						}

						self._requestRunning =
							false;

						if (error) {
							self._handleError(
								generation,
								error.code,
								error.message
							);

							return;
						}

						self._handlePacket(
							generation,
							packet
						);
					}
				);

			if (
				!started &&
				!callbackInvoked
			) {
				this._requestRunning =
					false;

				this._handleError(
					generation,
					'transport-not-started',
					'Module settings request was not started.'
				);
			}

			return started;
		};

	ModuleSettingsMonitor.prototype._handlePacket =
		function (
			generation,
			packet
		) {
			if (
				!packet ||
				packet.messageType !==
					MESSAGE_MODULE_SETTINGS ||
				!packet.payload ||
				packet.payload.length !==
					1
			) {
				this._handleError(
					generation,
					'invalid-settings-packet',
					'Bridge returned an invalid module settings packet.'
				);

				return false;
			}

			var mask =
				packet.payload[0];

			if (
				(mask & KNOWN_MASK) !==
					mask
			) {
				this._handleError(
					generation,
					'invalid-settings-mask',
					'Bridge returned unknown module settings bits.'
				);

				return false;
			}

			if (
				this._lastMask !==
					mask
			) {
				try {
					this._onSettingsChanged(
						this._decodeMask(
							mask
						)
					);
				} catch (callbackError) {
					this._handleError(
						generation,
						'settings-apply-error',
						String(
							callbackError
						)
					);

					return false;
				}

				this._lastMask =
					mask;

				this._log(
					'ModuleSettingsMonitor: CHANGE' +
						' | mask=' +
						mask
				);
			}

			this._lastErrorKey =
				null;

			this._schedule(
				POLL_INTERVAL,
				generation
			);

			return true;
		};

	ModuleSettingsMonitor.prototype._handleError =
		function (
			generation,
			code,
			message
		) {
			if (
				!this._running ||
				generation !==
					this._generation
			) {
				return;
			}

			this._requestRunning =
				false;

			var errorKey =
				String(
					code ||
						'unknown-error'
				) +
				'\u001f' +
				String(
					message ||
						''
				);

			if (
				errorKey !==
					this._lastErrorKey
			) {
				this._lastErrorKey =
					errorKey;

				this._log(
					'ModuleSettingsMonitor: ERROR' +
						' | code=' +
						String(
							code ||
								'unknown-error'
						) +
						' | message=' +
						String(
							message ||
								''
						)
				);
			}

			/*
			 * Keep the last accepted settings on transport errors.
			 */
			this._schedule(
				POLL_INTERVAL,
				generation
			);
		};

	ModuleSettingsMonitor.prototype._decodeMask =
		function (mask) {
			return {
				mask:
					mask,

				winrate:
					(mask & 1) !== 0,

				rank:
					(mask & 2) !== 0,

				adviser:
					(mask & 4) !== 0,

				heroDamage:
					(mask & 8) !== 0
			};
		};

	ThreatHud.ModuleSettingsMonitor =
		ModuleSettingsMonitor;

})(ThreatHud);
