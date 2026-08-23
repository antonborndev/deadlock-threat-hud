var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function ItemCatalog(rootPanel, logger) {
		this._rootPanel = rootPanel;
		this._items = [];

		this._log =
			typeof logger === 'function'
				? logger
				: function () {};
	}

	function findPanelsByType(rootPanel, panelType) {
		var result = [];

		if (!rootPanel || !rootPanel.IsValid()) {
			return result;
		}

		var queue = [rootPanel];

		for (var i = 0; i < queue.length; i++) {
			var panel = queue[i];

			if (!panel || !panel.IsValid()) {
				continue;
			}

			if (
				panel !== rootPanel &&
				panel.paneltype === panelType
			) {
				result.push(panel);
			}

			var children = panel.Children();

			for (var j = 0; j < children.length; j++) {
				queue.push(children[j]);
			}
		}

		return result;
	}

	function findItemName(shopCard) {
		var labels =
			shopCard.FindChildrenWithClassTraverse(
				'modName'
			);

		for (var i = 0; i < labels.length; i++) {
			var label = labels[i];

			if (
				label &&
				label.IsValid() &&
				label.paneltype === 'Label'
			) {
				var text =
					String(label.text || '');

				if (text) {
					return text;
				}
			}
		}

		return '';
	}

	function findTier(shopCard) {
		for (var tier = 1; tier <= 4; tier++) {
			if (
				shopCard.BHasClass(
					'ModTier' + tier
				)
			) {
				return tier;
			}
		}

		return 0;
	}

	function makeSignature(
		abilityClass,
		itemType,
		tier
	) {
		return (
			abilityClass +
			'|' +
			itemType +
			'|' +
			tier
		);
	}

	ItemCatalog.prototype.build = function () {
	var shopCards =
		findPanelsByType(
			this._rootPanel,
			'CitadelShopMod'
		);

	var items = [];
	var itemBySignature = {};

	var missingAbilityClass = 0;
	var missingItemType = 0;
	var missingTier = 0;
	var missingName = 0;

	for (var i = 0; i < shopCards.length; i++) {
		var shopCard = shopCards[i];

var abilityClass =
	shopCard.GetAttributeString(
		'abilityClass',
		''
	);

var itemType =
	shopCard.GetAttributeString(
		'itemType',
		''
	);

var tier = findTier(shopCard);
var itemName = findItemName(shopCard);

if (!abilityClass) {
	missingAbilityClass++;
}

if (!itemType) {
	missingItemType++;
}

		if (!tier) {
			missingTier++;
		}

		if (!itemName) {
			missingName++;
		}

if (
	!abilityClass ||
	!itemType ||
	!tier ||
	!itemName
) {
	continue;
}

var signature =
	String(abilityClass) +
	'|' +
	String(itemType) +
	'|' +
	String(tier);

		if (itemBySignature[signature]) {
			continue;
		}

		var item = {
			name: itemName,
			abilityClass: abilityClass,
			itemType: itemType,
			tier: tier,
			signature: signature
		};

		itemBySignature[signature] = item;
		items.push(item);
	}

	this._items = items;

	this._log(
		'ItemCatalog: cards=' +
		shopCards.length +
		' | noAbility=' +
		missingAbilityClass +
		' | noType=' +
		missingItemType +
		' | noTier=' +
		missingTier +
		' | noName=' +
		missingName +
		' | saved=' +
		items.length
	);

	return items.length;
};
	ItemCatalog.prototype.getItems = function () {
		var result = [];

		for (var i = 0; i < this._items.length; i++) {
			var item = this._items[i];

			result.push({
				name: item.name,
				abilityClass: item.abilityClass,
				itemType: item.itemType,
				tier: item.tier,
				signature: item.signature
			});
		}

		return result;
	};

	ThreatHud.ItemCatalog = ItemCatalog;
    ItemCatalog.prototype.debugRejectedCards = function () {
        var shopCards =
            findPanelsByType(
                this._rootPanel,
                'CitadelShopMod'
            );

        var firstItemBySignature = {};

        var emptyCards = 0;
        var incompleteCards = 0;
        var duplicateCards = 0;
        var collisions = 0;
        var uniqueCards = 0;

        for (var i = 0; i < shopCards.length; i++) {
            var shopCard = shopCards[i];

            var abilityClass =
                shopCard.GetAttributeString(
                    'abilityClass',
                    ''
                );

            var itemType =
                shopCard.GetAttributeString(
                    'itemType',
                    ''
                );

            var tier =
                findTier(shopCard);

            var itemName =
                findItemName(shopCard);

            /*
            * Completely empty shop placeholders
            * are not relevant.
            */
            if (
                !abilityClass &&
                !itemType &&
                !tier &&
                !itemName
            ) {
                emptyCards++;
                continue;
            }

            /*
            * The card is partially populated, but build()
            * cannot create a complete signature.
            */
            if (
                !abilityClass ||
                !itemType ||
                !tier ||
                !itemName
            ) {
                incompleteCards++;

                this._log(
                    'CATALOG REJECT: ' +
                    'name=' +
                    (itemName || '<missing>') +
                    ' | ability=' +
                    (abilityClass || '<missing>') +
                    ' | type=' +
                    (itemType || '<missing>') +
                    ' | tier=' +
                    (tier || '<missing>') +
                    ' | panelId=' +
                    (shopCard.id || '<empty>')
                );

                continue;
            }

            var signature =
                makeSignature(
                    abilityClass,
                    itemType,
                    tier
                );

            var firstItem =
                firstItemBySignature[signature];

            if (!firstItem) {
                firstItemBySignature[signature] = {
                    name: itemName,
                    panelId: shopCard.id || ''
                };

                uniqueCards++;
                continue;
            }

            if (firstItem.name === itemName) {
                duplicateCards++;

                this._log(
                    'CATALOG DUPLICATE: ' +
                    itemName +
                    ' | signature=' +
                    signature +
                    ' | firstPanel=' +
                    (firstItem.panelId || '<empty>') +
                    ' | duplicatePanel=' +
                    (shopCard.id || '<empty>')
                );

                continue;
            }

            collisions++;

            this._log(
                'CATALOG COLLISION: ' +
                    signature +
                    ' | "' +
                    firstItem.name +
                    '" / "' +
                    itemName +
                    '"'
            );
        }

        this._log(
            'Catalog diagnostic:' +
                ' cards=' +
                shopCards.length +
                ' | empty=' +
                emptyCards +
                ' | incomplete=' +
                incompleteCards +
                ' | unique=' +
                uniqueCards +
                ' | duplicates=' +
                duplicateCards +
                ' | collisions=' +
                collisions
        );
    };

})(ThreatHud);