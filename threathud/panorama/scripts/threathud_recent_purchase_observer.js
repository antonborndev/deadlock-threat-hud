var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function RecentPurchaseObserver(
		topPanel,
		purchaseHistory,
		logger
	) {
		this._topPanel =
			topPanel;

		this._purchaseHistory =
			purchaseHistory;

		this._log =
			typeof logger ===
				'function'
				? logger
				: function () {};

		this._container =
			null;

		this._running =
			false;

		this._interval =
			0.5;
	}

	RecentPurchaseObserver.prototype
		._findContainer =
		function () {
			if (
				this._container &&
				this._container.IsValid()
			) {
				return this._container;
			}

			if (
				!this._topPanel ||
				!this._topPanel.IsValid()
			) {
				return null;
			}

			this._container =
				this._topPanel
					.FindChildTraverse(
						'RecentPurchasesContainer'
					);

			return this._container;
		};

	RecentPurchaseObserver.prototype
		._readPurchase =
		function (purchasePanel) {
			if (
				!purchasePanel ||
				!purchasePanel.IsValid()
			) {
				return null;
			}

			var heroLabel =
				purchasePanel
					.FindChildTraverse(
						'ThreatHudRecentHeroName'
					);

			var itemLabel =
				ThreatHud.PanelUtils
					.findDirectChildByClass(
						purchasePanel,
						'recentModPurchaseName'
					);

			var timeLabel =
				ThreatHud.PanelUtils
					.findDirectChildByClass(
						purchasePanel,
						'recentTimePurchased'
					);

			if (
				!heroLabel ||
				!itemLabel
			) {
				return null;
			}

			var heroName =
				heroLabel.text || '';

			var itemName =
				itemLabel.text || '';

			var purchaseTime =
				timeLabel
					? timeLabel.text || ''
					: '';

			if (
				!heroName ||
				!itemName
			) {
				return null;
			}

			return {
				heroName:
					heroName,

				itemName:
					itemName,

				purchaseTime:
					purchaseTime
			};
		};

	RecentPurchaseObserver.prototype
		._processPurchase =
		function (purchase) {
			var internalId =
				this._purchaseHistory
					.setPurchase({
						time:
							purchase.purchaseTime,

						heroName:
							purchase.heroName,

						itemName:
							purchase.itemName
					});

			if (!internalId) {
				return false;
			}

			var savedPurchase =
				this._purchaseHistory
					.getPurchase(
						internalId
					);

			this._log(
				savedPurchase.internalId +
				' | ' +
				savedPurchase.time +
				' | ' +
				savedPurchase.heroName +
				' bought ' +
				savedPurchase.itemName
			);

			/*
			 * This second log already existed in the controller.
			 * For now, keep the behavior unchanged.
			 * After the refactoring is complete, it
			 * can be removed as a duplicate.
			 */
			this._log(
				purchase.heroName +
				' bought ' +
				purchase.itemName
			);

			return true;
		};

	RecentPurchaseObserver.prototype
		._scheduleNext =
		function () {
			if (!this._running) {
				return;
			}

			var self = this;

			$.Schedule(
				this._interval,
				function () {
					self._scan();
				}
			);
		};

	RecentPurchaseObserver.prototype
		._scan =
		function () {
			if (!this._running) {
				return;
			}

			var container =
				this._findContainer();

			if (
				container &&
				container.IsValid()
			) {
				var purchases =
					container.Children();

				for (
					var i = 0;
					i < purchases.length;
					i++
				) {
					if (
						!purchases[i]
							.BHasClass(
								'recentPurchase'
							)
					) {
						continue;
					}

					var purchase =
						this._readPurchase(
							purchases[i]
						);

					if (!purchase) {
						continue;
					}

					this._processPurchase(
						purchase
					);
				}
			}

			this._scheduleNext();
		};

	RecentPurchaseObserver.prototype.start =
		function () {
			if (this._running) {
				return false;
			}

			this._running = true;

			this._log(
				'Recent purchases observer started'
			);

			this._scan();

			return true;
		};

	RecentPurchaseObserver.prototype.stop =
		function () {
			this._running = false;
		};

	ThreatHud.RecentPurchaseObserver =
		RecentPurchaseObserver;

})(ThreatHud);