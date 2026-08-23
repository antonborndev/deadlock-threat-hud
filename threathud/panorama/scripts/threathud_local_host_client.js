var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var HEADER_SIZE = 8;
	var HEADER_CHUNKS = 4;
	var BYTES_PER_CHUNK = 2;
	var DIMENSION_BASE = 16;

	var MAX_PAYLOAD_BYTES = 8192;
	var MAX_CONCURRENT_REQUESTS = 6;

	var CHUNK_POLL_INTERVAL = 0.05;
	var CHUNK_MAX_ATTEMPTS = 700;

	var MESSAGE_CURRENT_MATCH_PLAYERS = 1;

	var MESSAGE_CURRENT_MATCH_PLAYER_IDENTITIES = 3;

	function LocalHostClient(
		context,
		baseUrl,
		logger
	) {
		this._context = context;

		this._baseUrl = String(
			baseUrl ||
				'http://127.0.0.1:28741'
		).replace(
			/\/+$/,
			''
		);

		this._log =
			typeof logger === 'function'
				? logger
				: function () {};

		this._generation = 1;
		this._activePanels = [];
		this._lastSessionOrdinal = 0;
	}

	LocalHostClient.prototype
		.getCurrentMatchPlayers =
		function (callback) {
			var self = this;

			return this._requestPacket(
				'current-match',
				{},
				function (
					error,
					packet
				) {
					if (error) {
						self._invokeCallback(
							callback,
							error,
							null
						);

						return;
					}

					if (
						packet.messageType !==
						MESSAGE_CURRENT_MATCH_PLAYERS
					) {
						self._invokeCallback(
							callback,

							self._createError(
								'unexpected-message-type',

								'Bridge returned an unexpected ' +
									'message type.',

								packet.messageType
							),

							null
						);

						return;
					}

					try {
						self._invokeCallback(
							callback,
							null,

							self
								._decodeCurrentMatchPayload(
									packet.payload
								)
						);
					} catch (
						decodeError
					) {
						self._invokeCallback(
							callback,

							self._createError(
								'invalid-current-match-payload',

								'Failed to parse ' +
									'current-match payload.',

								String(
									decodeError
								)
							),

							null
						);
					}
				}
			);
		};

	LocalHostClient.prototype
	.getCurrentMatchPlayerIdentities =
	function (callback) {
		var self = this;

		return this._requestPacket(
			'current-match-identities',
			{},

			function (
				error,
				packet
			) {
				if (error) {
					self._invokeCallback(
						callback,
						error,
						null
					);

					return;
				}

				if (
					packet.messageType !==
						MESSAGE_CURRENT_MATCH_PLAYER_IDENTITIES
				) {
					self._invokeCallback(
						callback,

						self._createError(
							'unexpected-message-type',

							'Bridge returned an unexpected ' +
								'identity message type.',

							packet.messageType
						),

						null
					);

					return;
				}

				try {
					self._invokeCallback(
						callback,
						null,

						self
							._decodeCurrentMatchIdentitiesPayload(
								packet.payload
							)
					);
				} catch (
					decodeError
				) {
					self._invokeCallback(
						callback,

						self._createError(
							'invalid-current-match-identities-payload',

							'Failed to parse ' +
								'identity payload.',

							String(
								decodeError
							)
						),

						null
					);
				}
			}
		);
	};

	LocalHostClient.prototype.requestPacket =
	function (
		channel,
		parameters,
		callback
	) {
		return this._requestPacket(
			channel,
			parameters || {},
			callback
		);
	};

	LocalHostClient.prototype.dispose =
		function () {
			this._generation++;

			for (
				var index = 0;
				index <
					this._activePanels.length;
				index++
			) {
				this._deletePanel(
					this._activePanels[index]
				);
			}

			this._activePanels = [];
		};

	LocalHostClient.prototype
		._requestPacket =
		function (
			channel,
			parameters,
			callback
		) {
			var self = this;

			var generation =
				this._generation;

			var session =
				this._createSessionToken();

			var headerIndices =
				[
					0,
					1,
					2,
					3
				];

			this._loadChunks(
				channel,
				session,
				headerIndices,
				parameters,
				generation,

				function (
					headerError,
					headerChunkBytes
				) {
					if (headerError) {
						self._invokeCallback(
							callback,
							headerError,
							null
						);

						return;
					}

					var headerBytes =
						self._flattenChunkBytes(
							headerIndices,
							headerChunkBytes,
							HEADER_SIZE
						);

					var header;

					try {
						header =
							self._parseHeader(
								headerBytes
							);
					} catch (
						headerDecodeError
					) {
						self._invokeCallback(
							callback,

							self._createError(
								'invalid-header',

								'Failed to parse ' +
									'transport header.',

								String(
									headerDecodeError
								)
							),

							null
						);

						return;
					}

					if (
						header.payloadLength >
						MAX_PAYLOAD_BYTES
					) {
						self._invokeCallback(
							callback,

							self._createError(
								'payload-too-large',

								'Transport payload exceeds ' +
									'the allowed size.',

								header.payloadLength
							),

							null
						);

						return;
					}

					var packetLength =
						HEADER_SIZE +
						header.payloadLength;

					var chunkCount =
						Math.ceil(
							packetLength /
								BYTES_PER_CHUNK
						);

					var remainingIndices =
						[];

					for (
						var chunkIndex =
							HEADER_CHUNKS;

						chunkIndex <
							chunkCount;

						chunkIndex++
					) {
						remainingIndices.push(
							chunkIndex
						);
					}

					self._loadChunks(
						channel,
						session,
						remainingIndices,
						parameters,
						generation,

						function (
							payloadError,
							payloadChunkBytes
						) {
							if (payloadError) {
								self._invokeCallback(
									callback,
									payloadError,
									null
								);

								return;
							}

							var allChunkBytes =
								{};

							self._copyChunkMap(
								headerChunkBytes,
								allChunkBytes
							);

							self._copyChunkMap(
								payloadChunkBytes,
								allChunkBytes
							);

							var packetBytes =
								self._flattenChunkBytes(
									self
										._buildSequentialIndices(
											chunkCount
										),

									allChunkBytes,
									packetLength
								);

							var payload =
								packetBytes.slice(
									HEADER_SIZE
								);

							var actualCrc =
								self._crc16(
									payload
								);

							if (
								actualCrc !==
								header.payloadCrc
							) {
								self._invokeCallback(
									callback,

									self._createError(
										'crc-mismatch',

										'Transport packet ' +
											'is corrupted.',

										'expected=' +
											header.payloadCrc +
											', actual=' +
											actualCrc
									),

									null
								);

								return;
							}

							self._invokeCallback(
								callback,
								null,

								{
									version:
										header.version,

									messageType:
										header.messageType,

									payload:
										payload,

									session:
										session
								}
							);
						}
					);
				}
			);

			return true;
		};

	LocalHostClient.prototype
		._loadChunks =
		function (
			channel,
			session,
			indices,
			parameters,
			generation,
			callback
		) {
			var self = this;

			var result = {};
			var nextIndex = 0;
			var activeCount = 0;
			var completedCount = 0;
			var finished = false;

			if (
				indices.length === 0
			) {
				this._invokeCallback(
					callback,
					null,
					result
				);

				return;
			}

			function finish(error) {
				if (finished) {
					return;
				}

				finished = true;

				self._invokeCallback(
					callback,
					error || null,
					error
						? null
						: result
				);
			}

			function pump() {
				if (
					finished ||
					generation !==
						self._generation
				) {
					return;
				}

				while (
					activeCount <
						MAX_CONCURRENT_REQUESTS &&
					nextIndex <
						indices.length
				) {
					(function (
						chunkIndex
					) {
						activeCount++;

						self._loadChunk(
							channel,
							session,
							chunkIndex,
							parameters,
							generation,

							function (
								error,
								bytes
							) {
								activeCount--;

								if (finished) {
									return;
								}

								if (error) {
									finish(
										error
									);

									return;
								}

								result[
									chunkIndex
								] = bytes;

								completedCount++;

								if (
									completedCount ===
									indices.length
								) {
									finish(
										null
									);

									return;
								}

								pump();
							}
						);
					})(
						indices[
							nextIndex
						]
					);

					nextIndex++;
				}
			}

			pump();
		};

	LocalHostClient.prototype
		._loadChunk =
		function (
			channel,
			session,
			chunkIndex,
			parameters,
			generation,
			callback
		) {
			var self = this;
			var completed = false;
			var panel = null;

			function finish(
				error,
				bytes
			) {
				if (completed) {
					return;
				}

				completed = true;

				self._removePanelReference(
					panel
				);

				self._deletePanel(
					panel
				);

				self._invokeCallback(
					callback,
					error || null,
					bytes || null
				);
			}

			if (
				generation !==
				this._generation
			) {
				finish(
					this._createError(
						'cancelled',
						'Transport request was canceled.',
						null
					),
					null
				);

				return;
			}

			try {
				panel =
					$.CreatePanel(
						'Image',
						this._context,

						'ThreatHudTransport_' +
							chunkIndex +
							'_' +
							String(
								Date.now()
							)
					);
			} catch (
				createError
			) {
				finish(
					this._createError(
						'panel-create-error',

						'Failed to create ' +
							'Image panel.',

						String(
							createError
						)
					),
					null
				);

				return;
			}

			if (!panel) {
				finish(
					this._createError(
						'panel-create-null',
						'$.CreatePanel returned null.',
						null
					),
					null
				);

				return;
			}

			this._activePanels.push(
				panel
			);

			try {
				panel.hittest = false;
				panel.visible = true;
				panel.enabled = true;

				panel.style.width =
					'fit-children';

				panel.style.height =
					'fit-children';

				panel.style.opacity =
					'0.01';

				panel.style.horizontalAlign =
					'left';

				panel.style.verticalAlign =
					'top';

				panel.style.marginLeft =
					'1px';

				panel.style.marginTop =
					'1px';
			} catch (
				styleError
			) {
				finish(
					this._createError(
						'panel-style-error',

						'Failed to configure ' +
							'Image panel.',

						String(
							styleError
						)
					),
					null
				);

				return;
			}

			var url =
				this._buildChunkUrl(
					channel,
					session,
					chunkIndex,
					parameters
				);

			try {
				panel.SetImage(
					url
				);
			} catch (
				setImageError
			) {
				finish(
					this._createError(
						'set-image-error',

						'Image.SetImage ended with an ' +
							'error.',

						String(
							setImageError
						)
					),
					null
				);

				return;
			}

			function poll(attempt) {
				if (completed) {
					return;
				}

				if (
					generation !==
					self._generation
				) {
					finish(
						self._createError(
							'cancelled',

							'Transport request ' +
								'was canceled.',

							null
						),
						null
					);

					return;
				}

				var decoded =
					self._decodePanelBytes(
						panel
					);

				if (decoded) {
					finish(
						null,
						decoded
					);

					return;
				}

				if (
					attempt >=
					CHUNK_MAX_ATTEMPTS
				) {
					finish(
						self._createError(
							'chunk-timeout',

							'Image loader did not load ' +
								'transport chunk.',

							'chunk=' +
								chunkIndex
						),
						null
					);

					return;
				}

				$.Schedule(
					CHUNK_POLL_INTERVAL,

					function () {
						poll(
							attempt + 1
						);
					}
				);
			}

			$.Schedule(
				CHUNK_POLL_INTERVAL,

				function () {
					poll(
						1
					);
				}
			);
		};

	LocalHostClient.prototype
		._decodePanelBytes =
		function (panel) {
			var contentWidth =
				Number(
					panel.contentwidth
				) || 0;

			var contentHeight =
				Number(
					panel.contentheight
				) || 0;

			if (
				contentWidth <= 0 ||
				contentHeight <= 0
			) {
				return null;
			}

			var scaleX =
				Number(
					panel.actualuiscale_x
				) || 1;

			var scaleY =
				Number(
					panel.actualuiscale_y
				) || 1;

			var decodedWidth =
				Math.round(
					contentWidth /
						scaleX
				);

			var decodedHeight =
				Math.round(
					contentHeight /
						scaleY
				);

			var byte0 =
				decodedWidth -
				DIMENSION_BASE;

			var byte1 =
				decodedHeight -
				DIMENSION_BASE;

			if (
				byte0 < 0 ||
				byte0 > 255 ||
				byte1 < 0 ||
				byte1 > 255
			) {
				return null;
			}

			return [
				byte0,
				byte1
			];
		};

	LocalHostClient.prototype
		._parseHeader =
		function (bytes) {
			if (
				bytes.length !==
				HEADER_SIZE
			) {
				throw new Error(
					'Invalid header size: ' +
					bytes.length
				);
			}

			if (
				bytes[0] !== 0x54 ||
				bytes[1] !== 0x48
			) {
				throw new Error(
					'Invalid transport magic.'
				);
			}

			if (bytes[2] !== 1)
			{
				throw new Error(
					'Unsupported ' +
					'protocol version: ' +
					bytes[2]
				);
			}

			return {
				version:
					bytes[2],

				messageType:
					bytes[3],

				payloadLength:
					bytes[4] +
					bytes[5] *
						256,

				payloadCrc:
					bytes[6] +
					bytes[7] *
						256
			};
		};

	LocalHostClient.prototype
		._decodeCurrentMatchPayload =
		function (payload) {
			if (payload.length < 1)
			{
				throw new Error(
					'Payload is empty.'
				);
			}

			var playerCount =
				payload[0];

			var expectedLength =
				1 +
				playerCount *
					5;

			if (
				payload.length !==
				expectedLength
			) {
				throw new Error(
					'Invalid payload size: ' +
					'expected=' +
					expectedLength +
					', actual=' +
					payload.length
				);
			}

			var players = [];

			for (
				var index = 0;
				index < playerCount;
				index++
			) {
				var offset =
					1 +
					index *
						5;

				var accountId =
					payload[offset] +
					payload[offset + 1] *
						256 +
					payload[offset + 2] *
						65536 +
					payload[offset + 3] *
						16777216;

				var flags =
					payload[
						offset + 4
					];

				players.push(
					{
						accountId:
							accountId,

						accountIdText:
							String(
								accountId
							),

						isLocal:
							(
								flags &
								1
							) !== 0
					}
				);
			}

			return {
				count:
					players.length,

				players:
					players
			};
		};


	LocalHostClient.prototype
	._decodeCurrentMatchIdentitiesPayload =
	function (payload) {
		if (payload.length < 1) {
			throw new Error(
				'Identity payload is empty.'
			);
		}

		var playerCount =
			payload[0];

		var players =
			[];

		var offset =
			1;

		for (
			var index = 0;
			index < playerCount;
			index++
		) {
			/*
			 * Minimum entry:
			 *
			 * uint32 accountID
			 * byte flags
			 * byte nameLength
			 */
			if (
				offset + 6 >
					payload.length
			) {
				throw new Error(
					'Identity entry is truncated' +
						' | index=' +
						index +
						' | offset=' +
						offset +
						' | payloadLength=' +
						payload.length
				);
			}

			var accountId =
				payload[offset] +
				payload[offset + 1] *
					256 +
				payload[offset + 2] *
					65536 +
				payload[offset + 3] *
					16777216;

			offset +=
				4;

			var flags =
				payload[offset];

			offset +=
				1;

			var nameLength =
				payload[offset];

			offset +=
				1;

			if (
				offset + nameLength >
					payload.length
			) {
				throw new Error(
					'PersonaName extends beyond the payload' +
						' | index=' +
						index +
						' | nameLength=' +
						nameLength +
						' | offset=' +
						offset +
						' | payloadLength=' +
						payload.length
				);
			}

			var nameBytes =
				payload.slice(
					offset,
					offset + nameLength
				);

			offset +=
				nameLength;

			var personaName =
				this._decodeUtf8(
					nameBytes
				);

			players.push({
				accountId:
					accountId,

				accountIdText:
					String(
						accountId
					),

				isLocal:
					(
						flags &
						1
					) !== 0,

				personaName:
					personaName,

				personaNameByteLength:
					nameLength
			});
		}

		if (
			offset !==
				payload.length
		) {
			throw new Error(
				'Extra bytes remain after identity entries' +
					' | parsed=' +
					offset +
					' | payloadLength=' +
					payload.length
			);
		}

		return {
			count:
				players.length,

			players:
				players
		};
	};

LocalHostClient.prototype
	._decodeUtf8 =
	function (bytes) {
		var result =
			'';

		var index =
			0;

		while (
			index < bytes.length
		) {
			var byte0 =
				bytes[index];

			var codePoint;

			if (
				byte0 <=
					0x7F
			) {
				codePoint =
					byte0;

				index +=
					1;
			} else if (
				byte0 >= 0xC2 &&
				byte0 <= 0xDF
			) {
				if (
					index + 1 >=
						bytes.length
				) {
					throw new Error(
						'Truncated two-byte UTF-8 sequence.'
					);
				}

				var byte1 =
					bytes[index + 1];

				if (
					(byte1 & 0xC0) !==
						0x80
				) {
					throw new Error(
						'Invalid UTF-8 continuation byte.'
					);
				}

				codePoint =
					(
						(byte0 & 0x1F) <<
						6
					) |
					(byte1 & 0x3F);

				index +=
					2;
			} else if (
				byte0 >= 0xE0 &&
				byte0 <= 0xEF
			) {
				if (
					index + 2 >=
						bytes.length
				) {
					throw new Error(
						'Truncated three-byte UTF-8 sequence.'
					);
				}

				var byte2_1 =
					bytes[index + 1];

				var byte2_2 =
					bytes[index + 2];

				if (
					(byte2_1 & 0xC0) !== 0x80 ||
					(byte2_2 & 0xC0) !== 0x80
				) {
					throw new Error(
						'Invalid three-byte UTF-8 sequence.'
					);
				}

				if (
					byte0 === 0xE0 &&
					byte2_1 < 0xA0
				) {
					throw new Error(
						'Overlong UTF-8 sequence detected.'
					);
				}

				if (
					byte0 === 0xED &&
					byte2_1 >= 0xA0
				) {
					throw new Error(
						'UTF-8 contains a surrogate code point.'
					);
				}

				codePoint =
					(
						(byte0 & 0x0F) <<
						12
					) |
					(
						(byte2_1 & 0x3F) <<
						6
					) |
					(byte2_2 & 0x3F);

				index +=
					3;
			} else if (
				byte0 >= 0xF0 &&
				byte0 <= 0xF4
			) {
				if (
					index + 3 >=
						bytes.length
				) {
					throw new Error(
						'Truncated four-byte UTF-8 sequence.'
					);
				}

				var byte3_1 =
					bytes[index + 1];

				var byte3_2 =
					bytes[index + 2];

				var byte3_3 =
					bytes[index + 3];

				if (
					(byte3_1 & 0xC0) !== 0x80 ||
					(byte3_2 & 0xC0) !== 0x80 ||
					(byte3_3 & 0xC0) !== 0x80
				) {
					throw new Error(
						'Invalid four-byte UTF-8 sequence.'
					);
				}

				if (
					byte0 === 0xF0 &&
					byte3_1 < 0x90
				) {
					throw new Error(
						'Overlong four-byte UTF-8 sequence detected.'
					);
				}

				if (
					byte0 === 0xF4 &&
					byte3_1 > 0x8F
				) {
					throw new Error(
						'UTF-8 code point exceeds U+10FFFF.'
					);
				}

				codePoint =
					(
						(byte0 & 0x07) <<
						18
					) |
					(
						(byte3_1 & 0x3F) <<
						12
					) |
					(
						(byte3_2 & 0x3F) <<
						6
					) |
					(byte3_3 & 0x3F);

				index +=
					4;
			} else {
				throw new Error(
					'Invalid leading UTF-8 byte: ' +
						byte0
				);
			}

			if (
				codePoint <=
					0xFFFF
			) {
				result +=
					String.fromCharCode(
						codePoint
					);
			} else {
				codePoint -=
					0x10000;

				result +=
					String.fromCharCode(
						0xD800 +
							(
								codePoint >>
								10
							),

						0xDC00 +
							(
								codePoint &
								0x3FF
							)
					);
			}
		}

		return result;
	};
	
	LocalHostClient.prototype
		._buildChunkUrl =
		function (
			channel,
			session,
			chunkIndex,
			parameters
		) {
			var query =
				[
					'channel=' +
						encodeURIComponent(
							channel
						),

					'session=' +
						encodeURIComponent(
							session
						),

					'chunk=' +
						encodeURIComponent(
							String(
								chunkIndex
							)
						),

					'cacheBust=' +
						encodeURIComponent(
							String(
								Date.now()
							) +
							'_' +
							chunkIndex
						)
				];

			for (
				var key in parameters
			) {
				if (
					!parameters
						.hasOwnProperty(
							key
						)
				) {
					continue;
				}

				query.push(
					encodeURIComponent(
						key
					) +
					'=' +
					encodeURIComponent(
						String(
							parameters[key]
						)
					)
				);
			}

			return (
				this._baseUrl +
				'/bridge.png?' +
				query.join(
					'&'
				)
			);
		};

	LocalHostClient.prototype
		._createSessionToken =
		function () {
			/*
			 * The value stays below Number.MAX_SAFE_INTEGER while remaining
			 * strictly increasing for every request from this client. Bridge
			 * uses it to ignore late service results from an older roster.
			 */
			var candidate =
				Date.now() *
				1000;

			if (
				candidate <=
				this._lastSessionOrdinal
			) {
				candidate =
					this._lastSessionOrdinal +
					1;
			}

			this._lastSessionOrdinal =
				candidate;

			return String(
				candidate
			);
		};

	LocalHostClient.prototype
		._flattenChunkBytes =
		function (
			indices,
			chunkMap,
			maximumLength
		) {
			var bytes = [];

			for (
				var index = 0;
				index < indices.length;
				index++
			) {
				var chunkBytes =
					chunkMap[
						indices[index]
					];

				if (
					!chunkBytes ||
					chunkBytes.length !==
						BYTES_PER_CHUNK
				) {
					throw new Error(
						'Missing chunk ' +
						indices[index]
					);
				}

				bytes.push(
					chunkBytes[0]
				);

				bytes.push(
					chunkBytes[1]
				);
			}

			return bytes.slice(
				0,
				maximumLength
			);
		};

	LocalHostClient.prototype
		._copyChunkMap =
		function (
			source,
			target
		) {
			for (
				var key in source
			) {
				if (
					source
						.hasOwnProperty(
							key
						)
				) {
					target[key] =
						source[key];
				}
			}
		};

	LocalHostClient.prototype
		._buildSequentialIndices =
		function (count) {
			var indices = [];

			for (
				var index = 0;
				index < count;
				index++
			) {
				indices.push(
					index
				);
			}

			return indices;
		};

	LocalHostClient.prototype
		._crc16 =
		function (bytes) {
			var crc =
				0xFFFF;

			for (
				var index = 0;
				index < bytes.length;
				index++
			) {
				crc ^=
					bytes[index] <<
					8;

				for (
					var bit = 0;
					bit < 8;
					bit++
				) {
					crc =
						(
							crc &
							0x8000
						) !== 0

							? (
								(
									crc << 1
								) ^
								0x1021
							) &
								0xFFFF

							: (
								crc << 1
							) &
								0xFFFF;
				}
			}

			return crc;
		};

	LocalHostClient.prototype
		._removePanelReference =
		function (panel) {
			if (!panel) {
				return;
			}

			for (
				var index =
					this
						._activePanels
						.length -
					1;

				index >= 0;

				index--
			) {
				if (
					this._activePanels[
						index
					] === panel
				) {
					this._activePanels.splice(
						index,
						1
					);

					return;
				}
			}
		};

	LocalHostClient.prototype
		._deletePanel =
		function (panel) {
			if (!panel) {
				return;
			}

			try {
				if (
					typeof panel.IsValid ===
						'function' &&
					!panel.IsValid()
				) {
					return;
				}
			} catch (
				validationError
			) {
				return;
			}

			try {
				panel.DeleteAsync(
					0.0
				);
			} catch (
				deleteError
			) {
				/*
				 * The panel may already have been removed
				 * together with the layout.
				 */
			}
		};

	LocalHostClient.prototype
		._createError =
		function (
			code,
			message,
			detail
		) {
			return {
				code:
					String(
						code ||
							'unknown-error'
					),

				message:
					String(
						message ||
							''
					),

				detail:
					detail ===
						undefined
							? null
							: detail
			};
		};

	LocalHostClient.prototype
		._invokeCallback =
		function (
			callback,
			error,
			result
		) {
			if (
				typeof callback ===
					'function'
			) {
				callback(
					error || null,
					result || null
				);
			}
		};

	ThreatHud.LocalHostClient =
		LocalHostClient;

})(ThreatHud);
