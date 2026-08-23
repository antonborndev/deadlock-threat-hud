var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	function normalizeName(value) {
		var text = String(value || '');

		try {
			if (typeof text.normalize === 'function') {
				text = text.normalize('NFKC');
			}
		} catch (normalizeError) {
			/*
			 * Panorama may not support
			 * String.normalize.
			 */
		}

		return text
			.replace(
				/[\u200B-\u200D\uFEFF]/g,
				''
			)
			.replace(
				/\s+/g,
				' '
			)
			.replace(
				/^\s+|\s+$/g,
				''
			)
			.toLowerCase();
	}

	function nameKey(value) {
		return (
			'name:' +
			normalizeName(value)
		);
	}

	ThreatHud.IdentityUtils = {
		normalizeName:
			normalizeName,

		nameKey:
			nameKey
	};

})(ThreatHud);