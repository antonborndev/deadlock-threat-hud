var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function isValidPanel(panel) {
		return !!(
			panel &&
			panel.IsValid()
		);
	}

	function getTopPanel(context) {
		if (!isValidPanel(context)) {
			return null;
		}

		var topPanel = context;
		var parent = topPanel.GetParent();

		while (parent) {
			topPanel = parent;
			parent = topPanel.GetParent();
		}

		return topPanel;
	}

	function findDirectChildByClass(
		panel,
		className
	) {
		if (!isValidPanel(panel)) {
			return null;
		}

		var children = panel.Children();

		for (
			var i = 0;
			i < children.length;
			i++
		) {
			if (
				isValidPanel(children[i]) &&
				children[i].BHasClass(
					className
				)
			) {
				return children[i];
			}
		}

		return null;
	}

	function findDirectChildrenByType(
		panel,
		panelType
	) {
		var result = [];

		if (!isValidPanel(panel)) {
			return result;
		}

		var children = panel.Children();

		for (
			var i = 0;
			i < children.length;
			i++
		) {
			if (
				isValidPanel(children[i]) &&
				children[i].paneltype ===
					panelType
			) {
				result.push(
					children[i]
				);
			}
		}

		return result;
	}

	function findPanelsByType(
		rootPanel,
		panelType
	) {
		var result = [];

		if (!isValidPanel(rootPanel)) {
			return result;
		}

		var queue = [rootPanel];

		for (
			var i = 0;
			i < queue.length;
			i++
		) {
			var panel = queue[i];

			if (!isValidPanel(panel)) {
				continue;
			}

			if (
				panel !== rootPanel &&
				panel.paneltype === panelType
			) {
				result.push(panel);
			}

			var children = panel.Children();

			for (
				var childIndex = 0;
				childIndex < children.length;
				childIndex++
			) {
				queue.push(
					children[childIndex]
				);
			}
		}

		return result;
	}

	function findFirstLabelByClass(
		rootPanel,
		className
	) {
		if (!isValidPanel(rootPanel)) {
			return null;
		}

		var matches =
			rootPanel
				.FindChildrenWithClassTraverse(
					className
				);

		for (
			var i = 0;
			i < matches.length;
			i++
		) {
			if (
				isValidPanel(matches[i]) &&
				matches[i].paneltype === 'Label'
			) {
				return matches[i];
			}
		}

		return null;
	}

	ThreatHud.PanelUtils = {
		isValidPanel:
			isValidPanel,

		getTopPanel:
			getTopPanel,

		findDirectChildByClass:
			findDirectChildByClass,

		findDirectChildrenByType:
			findDirectChildrenByType,

		findPanelsByType:
			findPanelsByType,

		findFirstLabelByClass:
			findFirstLabelByClass
	};

})(ThreatHud);