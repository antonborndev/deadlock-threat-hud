var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function ManualTest(options) {
		options = options || {};

		this._itemCatalog =
			options.itemCatalog || null;

		this._enemyTeam =
			options.enemyTeam || null;

		this._purchaseHistory =
			options.purchaseHistory || null;

		this._topPanel =
			options.topPanel || null;

		this._log =
			typeof options.logger ===
				'function'
				? options.logger
				: function () {};
	}

	ManualTest.prototype.run =
		function () {
			this._log(
				'Manual test: start'
			);

			try {
				/*
				 * ==================================
				 * START OF MANUAL TEST
				 * ==================================
				 */


				/*
				 * ==================================
				 * END OF MANUAL TEST
				 * ==================================
				 */

				this._log(
					'Manual test: completed'
				);
			} catch (error) {
				var errorText =
					String(error);

				if (
					error &&
					error.stack
				) {
					errorText =
						String(error.stack);
				}

				this._log(
					'Manual test ERROR: ' +
					errorText
				);
			}
		};

	ThreatHud.ManualTest = ManualTest;

})(ThreatHud);