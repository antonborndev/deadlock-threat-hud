var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function DebugControls(options) {
		options = options || {};

		this._context =
			options.context || null;

		this._manualTest =
			options.manualTest || null;

		this._logger =
			options.logger || null;

		this._log =
			typeof options.log ===
				'function'
				? options.log
				: function () {};
	}

	DebugControls.prototype._findButton =
		function (buttonId) {
			if (
				!this._context ||
				!this._context.IsValid()
			) {
				return null;
			}

			var button =
				this._context
					.FindChildTraverse(
						buttonId
					);

			if (
				!button ||
				!button.IsValid()
			) {
				return null;
			}

			return button;
		};

	DebugControls.prototype._bindRunButton =
		function () {
			var button =
				this._findButton(
					'ThreatHudManualTestButton'
				);

			if (!button) {
				this._log(
					'Manual test: button not found'
				);

				return false;
			}

			var manualTest =
				this._manualTest;

			button.SetPanelEvent(
				'onactivate',
				function () {
					if (manualTest) {
						manualTest.run();
					}
				}
			);

			this._log(
				'Manual test: button connected'
			);

			return true;
		};

	DebugControls.prototype._bindCopyButton =
		function () {
			var button =
				this._findButton(
					'ThreatHudCopyLogButton'
				);

			if (!button) {
				this._log(
					'Copy log: button not found'
				);

				return false;
			}

			var logger =
				this._logger;

			button.SetPanelEvent(
				'onactivate',
				function () {
					if (logger) {
						logger.copyToClipboard();
					}
				}
			);

			return true;
		};

	DebugControls.prototype.bind =
		function () {
			var runBound =
				this._bindRunButton();

			var copyBound =
				this._bindCopyButton();

			return (
				runBound &&
				copyBound
			);
		};

	ThreatHud.DebugControls =
		DebugControls;

})(ThreatHud);