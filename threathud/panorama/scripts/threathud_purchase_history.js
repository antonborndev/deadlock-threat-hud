var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';


    
	function PurchaseHistory() {
		this._purchases = {};
		this._idBySignature = {};
		this._nextId = 1;
	}

	PurchaseHistory.prototype.setPurchase = function (purchase) {
		if (!purchase) {
			return null;
		}

		var time = String(purchase.time || '');
		var heroName = String(purchase.heroName || '');
		var itemName = String(purchase.itemName || '');

		if (!heroName || !itemName) {
			return null;
		}

		var signature =
			time + '|' +
			heroName + '|' +
			itemName;

		if (this._idBySignature[signature]) {
			return null;
		}

		var internalId =
			'purchase_' + this._nextId;

		this._nextId++;

		this._purchases[internalId] = {
			internalId: internalId,
			time: time,
			heroName: heroName,
			itemName: itemName
		};

		this._idBySignature[signature] =
			internalId;

		return internalId;
	};

	PurchaseHistory.prototype.getPurchase = function (internalId) {
		var purchase =
			this._purchases[internalId];

		if (!purchase) {
			return null;
		}

		return {
			internalId: purchase.internalId,
			time: purchase.time,
			heroName: purchase.heroName,
			itemName: purchase.itemName
		};
	};

	PurchaseHistory.prototype.getAllPurchases = function () {
		var result = {};

		for (var internalId in this._purchases) {
			if (
				!Object.prototype.hasOwnProperty.call(
					this._purchases,
					internalId
				)
			) {
				continue;
			}

			var purchase =
				this._purchases[internalId];

			result[internalId] = {
				internalId: purchase.internalId,
				time: purchase.time,
				heroName: purchase.heroName,
				itemName: purchase.itemName
			};
		}

		return result;
	};

	ThreatHud.PurchaseHistory =
		PurchaseHistory;

})(ThreatHud);