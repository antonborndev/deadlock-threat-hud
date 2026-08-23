var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function HudLogger(context) {
		this._viewport =
			context.FindChildTraverse(
				'ThreatHudLogViewport'
			);

		this._textPanel =
			context.FindChildTraverse(
				'ThreatHudLogText'
			);

		this._lines = [];
	}

	HudLogger.prototype.write =
		function (message) {
			if (
				!this._textPanel ||
				!this._textPanel.IsValid()
			) {
				return;
			}

			var line =
				String(message);

			this._lines.push(line);

			this._textPanel.text =
				this._lines.join('\n');

			var self = this;

			$.Schedule(
				0.5,
				function () {
					if (
						self._viewport &&
						self._viewport.IsValid() &&
						typeof self._viewport
							.ScrollToBottom ===
							'function'
					) {
						self._viewport
							.ScrollToBottom();
					}
				}
			);
		};

	HudLogger.prototype.getWriter =
		function () {
			var self = this;

			return function (message) {
				self.write(message);
			};
		};

	HudLogger.prototype.getLines =
		function () {
			return this._lines.slice(0);
		};

	HudLogger.prototype.copyToClipboard =
		function () {
			if (this._lines.length === 0) {
				this.write(
					'Copy log: log is empty'
				);

				return false;
			}

			var logText =
				this._lines.join('\n');

			try {
				$.DispatchEvent(
					'CopyStringToClipboard',
					logText,
					null
				);

				this.write(
					'Copy log: copied lines: ' +
					this._lines.length
				);

				return true;
			} catch (error) {
				this.write(
					'Copy log ERROR: ' +
					String(error)
				);

				return false;
			}
		};

	HudLogger.prototype.clear =
		function () {
			this._lines = [];

			if (
				this._textPanel &&
				this._textPanel.IsValid()
			) {
				this._textPanel.text = '';
			}
		};

	ThreatHud.HudLogger = HudLogger;

})(ThreatHud);