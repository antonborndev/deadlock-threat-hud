var ThreatHud = ThreatHud || {};

(function (ThreatHud) {
	'use strict';

	var EXPECTED_PLAYERS =
		12;

	var ALLY_PLAYERS =
		6;

	var EXPECTED_OPTIONS =
		5;

	var MESSAGE_LANE_ADVISOR_RESULT =
		6;

	var RESULT_STATUS_PENDING =
		0;

	var RESULT_STATUS_READY =
		1;

	var RESULT_STATUS_FAILED =
		2;

	var OPTION_BYTES =
		16;

	var RESULT_POLL_INTERVAL =
		0.25;

	var RESULT_POLL_MAX_ATTEMPTS =
		140;

	var SWAP_INDEX_STAY =
		255;

	var FLAG_HAS_MATCH_DATA =
		1 << 0;

	var FLAG_HAS_NET_WORTH_DATA =
		1 << 1;

	var FLAG_IS_BEST =
		1 << 2;

	function LaneAdvisorClient(
		localHostClient,
		logger,
		resultHandler
	) {
		this._transport =
			localHostClient;

		this._log =
			typeof logger ===
				'function'
					? logger
					: function () {};

		this._resultHandler =
			typeof resultHandler ===
				'function'
					? resultHandler
					: function () {};

		this._lastFingerprint =
			null;

		this._lastRosterVersion =
			0;

		this._lastResult =
			null;

		this._resultGeneration =
			0;

		this._currentMatches =
			null;

		this._enabled =
			false;

		this._deferredMatches =
			null;
	}

	LaneAdvisorClient.prototype.startForMatches =
		function (
			matches,
			callback
		) {
			var validation =
				this._validateMatches(
					matches
				);

			if (validation.error) {
				this._invokeCallback(
					callback,
					validation.error,
					null
				);

				return false;
			}

			var localIndex =
				validation.localIndex;

			var fingerprint =
				this._buildFingerprint(
					matches,
					localIndex
				);

			var matchesSnapshot =
				matches.slice(
					0
				);

			this._currentMatches =
				matchesSnapshot;

			if (!this._enabled) {
				this._currentMatches =
					null;

				this._deferredMatches =
					matchesSnapshot;

				this._invokeCallback(
					callback,
					null,
					{
						started:
							false,

						deduplicated:
							false,

						disabled:
							true,

						localIndex:
							localIndex
					}
				);

				return false;
			}

			this._deferredMatches =
				null;

			if (
				fingerprint ===
					this._lastFingerprint
			) {
				if (this._lastResult) {
					this._notifyResult(
						null,
						this._lastResult,
						matchesSnapshot
					);
				}

				this._invokeCallback(
					callback,
					null,
					{
						started:
							false,

						deduplicated:
							true,

						localIndex:
							localIndex
					}
				);

				return false;
			}

			var rosterVersion =
				this._nextRosterVersion();

			var resultGeneration =
				++this._resultGeneration;

			var parameters = {
				count:
					EXPECTED_PLAYERS,

				localIndex:
					localIndex,

				version:
					String(
						rosterVersion
					)
			};

			for (
				var index = 0;
				index < EXPECTED_PLAYERS;
				index++
			) {
				parameters[
					'h' + index
				] =
					matches[index]
						.heroName;
			}

			this._lastFingerprint =
				fingerprint;

			this._lastResult =
				null;

			this._notifyResult(
				null,
				null,
				matchesSnapshot
			);

			this._log(
				'LaneAdvisorClient: REQUEST' +
					' | version=' +
						rosterVersion +
					' | localIndex=' +
						localIndex +
					' | hero=' +
						matches[localIndex]
							.heroName
			);

			var self =
				this;

			var transportStarted =
				this._transport
					.requestPacket(
						'lane-advisor-roster',
						parameters,

						function (
							error,
							packet
						) {
							if (
								resultGeneration !==
									self._resultGeneration
							) {
								return;
							}

							if (error) {
								self._handleResultError(
									resultGeneration,
									fingerprint,
									error,
									matchesSnapshot
								);

								return;
							}

							if (
								!packet ||
								packet.messageType !==
									MESSAGE_LANE_ADVISOR_RESULT ||
								!packet.payload ||
								packet.payload.length !==
									0
							) {
								self._handleResultError(
									resultGeneration,
									fingerprint,
									self._createError(
										'invalid-roster-ack',

										'Bridge returned an invalid Lane Advisor ACK.',

										packet
											? packet.messageType
											: null
									),
									matchesSnapshot
								);

								return;
							}

							self._log(
								'LaneAdvisorClient: ACK' +
									' | version=' +
										rosterVersion +
									' | localIndex=' +
										localIndex
							);

							self._pollResult(
								resultGeneration,
								fingerprint,
								rosterVersion,
								localIndex,
								1
							);
						}
					);

			if (!transportStarted) {
				var startError =
					this._createError(
						'transport-not-started',

						'Failed to start Lane Advisor transport.',

						null
					);

				this._handleResultError(
					resultGeneration,
					fingerprint,
					startError,
					matchesSnapshot
				);

				this._invokeCallback(
					callback,
					startError,
					null
				);

				return false;
			}

			this._invokeCallback(
				callback,
				null,
				{
					started:
						true,

					deduplicated:
						false,

					localIndex:
						localIndex,

					rosterVersion:
						rosterVersion
				}
			);

			return true;
		};

	LaneAdvisorClient.prototype.setEnabled =
		function (enabled) {
			var nextEnabled =
				!!enabled;

			if (
				this._enabled ===
					nextEnabled
			) {
				return false;
			}

			var deferredMatches =
				this._currentMatches
					? this._currentMatches.slice(
						0
					)
					: (
						this._deferredMatches
							? this._deferredMatches.slice(
								0
							)
							: null
					);

			this._enabled =
				nextEnabled;

			if (!nextEnabled) {
				this.stop();

				this._deferredMatches =
					deferredMatches;

				return true;
			}

			this._deferredMatches =
				null;

			if (deferredMatches) {
				this.startForMatches(
					deferredMatches,
					function () {}
				);
			}

			return true;
		};

	LaneAdvisorClient.prototype.stop =
		function () {
			var hadActiveWorkflow =
				this._lastFingerprint !== null ||
				this._lastResult !== null ||
				this._currentMatches !== null ||
				this._deferredMatches !== null;

			this._resultGeneration +=
				1;

			this._lastFingerprint =
				null;

			this._lastResult =
				null;

			this._currentMatches =
				null;

			this._deferredMatches =
				null;

			if (hadActiveWorkflow) {
				this._notifyResult(
					null,
					null,
					null
				);
			}

			return hadActiveWorkflow;
		};

	LaneAdvisorClient.prototype._pollResult =
		function (
			resultGeneration,
			fingerprint,
			rosterVersion,
			localIndex,
			attempt
		) {
			if (
				resultGeneration !==
					this._resultGeneration ||
				fingerprint !==
					this._lastFingerprint
			) {
				return false;
			}

			if (
				attempt >
					RESULT_POLL_MAX_ATTEMPTS
			) {
				this._handleResultError(
					resultGeneration,
					fingerprint,
					this._createError(
						'result-timeout',

						'Lane Advisor result was not ready in time.',

						rosterVersion
					),
					this._currentMatches
				);

				return false;
			}

			var self =
				this;

			var started =
				this._transport.requestPacket(
					'lane-advisor-roster',
					{
						mode:
							'result',

						version:
							String(
								rosterVersion
							)
					},

					function (
						error,
						packet
					) {
						if (
							resultGeneration !==
								self._resultGeneration ||
							fingerprint !==
								self._lastFingerprint
						) {
							return;
						}

						if (error) {
							self._handleResultError(
								resultGeneration,
								fingerprint,
								error,
								self._currentMatches
							);

							return;
						}

						if (
							!packet ||
							packet.messageType !==
								MESSAGE_LANE_ADVISOR_RESULT
						) {
							self._handleResultError(
								resultGeneration,
								fingerprint,
								self._createError(
									'unexpected-message-type',

									'Bridge returned an unexpected Lane Advisor result type.',

									packet
										? packet.messageType
										: null
								),
								self._currentMatches
							);

							return;
						}

						var result;

						try {
							result =
								self._decodeResultPayload(
									packet.payload
								);
						} catch (decodeError) {
							self._handleResultError(
								resultGeneration,
								fingerprint,
								self._createError(
									'invalid-result-payload',

									'Failed to parse Lane Advisor result payload.',

									String(
										decodeError
									)
								),
								self._currentMatches
							);

							return;
						}

						if (
							result.status ===
								'pending'
						) {
							$.Schedule(
								RESULT_POLL_INTERVAL,

								function () {
									self._pollResult(
										resultGeneration,
										fingerprint,
										rosterVersion,
										localIndex,
										attempt + 1
									);
								}
							);

							return;
						}

						if (
							result.status ===
								'failed'
						) {
							self._handleResultError(
								resultGeneration,
								fingerprint,
								self._createError(
									'calculation-failed',

									'Bridge failed to calculate the Lane Advisor result.',

									rosterVersion
								),
								self._currentMatches
							);

							return;
						}

						if (
							result.localIndex !==
								localIndex
						) {
							self._handleResultError(
								resultGeneration,
								fingerprint,
								self._createError(
									'local-index-mismatch',

									'Lane Advisor localIndex does not match.',

									'expected=' +
										localIndex +
										', actual=' +
										result.localIndex
								),
								self._currentMatches
							);

							return;
						}

						result.rosterVersion =
							rosterVersion;

						result.session =
							packet.session;

						self._lastResult =
							result;

						self._log(
							'LaneAdvisorClient: RESULT' +
								' | version=' +
									rosterVersion +
								' | localIndex=' +
									result.localIndex +
								' | options=' +
									result.options.length +
								' | pollAttempt=' +
									attempt
						);

						self._notifyResult(
							null,
							result,
							self._currentMatches
						);
					}
				);

			if (!started) {
				this._handleResultError(
					resultGeneration,
					fingerprint,
					this._createError(
						'result-transport-not-started',

						'Failed to start Lane Advisor result transport.',

						rosterVersion
					),
					this._currentMatches
				);

				return false;
			}

			return true;
		};

	LaneAdvisorClient.prototype._handleResultError =
		function (
			resultGeneration,
			fingerprint,
			error,
			matches
		) {
			if (
				resultGeneration !==
					this._resultGeneration
			) {
				return;
			}

			if (
				this._lastFingerprint ===
					fingerprint
			) {
				this._lastFingerprint =
					null;

				this._lastResult =
					null;
			}

			this._log(
				'LaneAdvisorClient: RESULT ERROR' +
					' | code=' +
						String(
							error && error.code
								? error.code
								: 'unknown-error'
						) +
					' | message=' +
						String(
							error && error.message
								? error.message
								: ''
						)
			);

			this._notifyResult(
				error,
				null,
				matches || null
			);
		};

	LaneAdvisorClient.prototype._decodeResultPayload =
		function (payload) {
			if (
				!payload ||
				payload.length < 1
			) {
				throw new Error(
					'Lane Advisor payload is empty.'
				);
			}

			var status =
				payload[0];

			if (
				status ===
					RESULT_STATUS_PENDING
			) {
				if (
					payload.length !==
						1
				) {
					throw new Error(
						'Invalid pending payload size.'
					);
				}

				return {
					status:
						'pending',

					localIndex:
						-1,

					options:
						[]
				};
			}

			if (
				status ===
					RESULT_STATUS_FAILED
			) {
				if (
					payload.length !==
						1
				) {
					throw new Error(
						'Invalid failed payload size.'
					);
				}

				return {
					status:
						'failed',

					localIndex:
						-1,

					options:
						[]
				};
			}

			if (
				status !==
					RESULT_STATUS_READY
			) {
				throw new Error(
					'Unknown Lane Advisor status: ' +
						status
				);
			}

			if (
				payload.length <
					3
			) {
				throw new Error(
					'Lane Advisor ready payload is truncated.'
				);
			}

			var localIndex =
				payload[1];

			var optionCount =
				payload[2];

			if (
				localIndex >=
					ALLY_PLAYERS
			) {
				throw new Error(
					'Invalid Lane Advisor localIndex: ' +
						localIndex
				);
			}

			if (
				optionCount !==
					EXPECTED_OPTIONS
			) {
				throw new Error(
					'Invalid number of Lane Advisor options' +
						' | expected=' +
						EXPECTED_OPTIONS +
						' | actual=' +
						optionCount
				);
			}

			var expectedLength =
				3 +
				optionCount *
					OPTION_BYTES;

			if (
				payload.length !==
					expectedLength
			) {
				throw new Error(
					'Invalid Lane Advisor payload size' +
						' | expected=' +
						expectedLength +
						' | actual=' +
						payload.length
				);
			}

			var options =
				[];

			var stayCount =
				0;

			var bestCount =
				0;

			var seenSwapIndexes =
				{};

			for (
				var index = 0;
				index < optionCount;
				index++
			) {
				var offset =
					3 +
					index *
						OPTION_BYTES;

				var rawSwapIndex =
					payload[offset];

				var swapWithIndex =
					rawSwapIndex ===
						SWAP_INDEX_STAY
						? null
						: rawSwapIndex;

				if (
					swapWithIndex !== null &&
					(
						swapWithIndex < 0 ||
						swapWithIndex >=
							ALLY_PLAYERS
					)
				) {
					throw new Error(
						'Invalid swapWithIndex: ' +
							swapWithIndex
					);
				}

				if (
					swapWithIndex ===
						null
				) {
					stayCount++;
				} else {
					var swapKey =
						String(
							swapWithIndex
						);

					if (
						seenSwapIndexes
							.hasOwnProperty(
								swapKey
							)
					) {
						throw new Error(
							'Duplicate swapWithIndex: ' +
								swapWithIndex
						);
					}

					seenSwapIndexes[
						swapKey
					] =
						true;
				}

				var flags =
					payload[
						offset + 1
					];

				var hasMatchData =
					(
						flags &
						FLAG_HAS_MATCH_DATA
					) !== 0;

				var hasNetWorthData =
					(
						flags &
						FLAG_HAS_NET_WORTH_DATA
					) !== 0;

				var isBest =
					(
						flags &
						FLAG_IS_BEST
					) !== 0;

				if (isBest) {
					bestCount++;
				}

				var scaledWinRate =
					payload[offset + 2] +
					payload[offset + 3] *
						256;

				var scaledNetWorthDiff =
					this._readInt32LittleEndian(
						payload,
						offset + 4
					);

				var matches =
					this._readUInt32LittleEndian(
						payload,
						offset + 8
					);

				var netWorthMatches =
					this._readUInt32LittleEndian(
						payload,
						offset + 12
					);

				if (
					hasMatchData &&
					scaledWinRate >
						10000
				) {
					throw new Error(
						'Invalid Lane Advisor winrate: ' +
							scaledWinRate
					);
				}

				if (
					!hasMatchData &&
					scaledWinRate !==
						0
				) {
					throw new Error(
						'Lane Advisor no-data option contains winrate.'
					);
				}

				if (
					hasMatchData &&
					matches ===
						0
				) {
					throw new Error(
						'Lane Advisor match-data option contains matches=0.'
					);
				}

				if (
					!hasMatchData &&
					matches !==
						0
				) {
					throw new Error(
						'Lane Advisor no-data option contains matches.'
					);
				}

				if (
					!hasNetWorthData &&
					scaledNetWorthDiff !==
						0
				) {
					throw new Error(
						'Lane Advisor no-data option contains souls diff.'
					);
				}

				if (
					hasNetWorthData &&
					netWorthMatches ===
						0
				) {
					throw new Error(
						'Lane Advisor net-worth option contains netWorthMatches=0.'
					);
				}

				if (
					!hasNetWorthData &&
					netWorthMatches !==
						0
				) {
					throw new Error(
						'Lane Advisor no-data option contains netWorthMatches.'
					);
				}

				if (
					isBest &&
					!hasNetWorthData
				) {
					throw new Error(
						'Lane Advisor BEST does not contain S15 data.'
					);
				}

				options.push({
					swapWithIndex:
						swapWithIndex,

					hasMatchData:
						hasMatchData,

					winRatePercent:
						hasMatchData
							? scaledWinRate /
								100.0
							: 0,

					matches:
						matches,

					hasNetWorthData:
						hasNetWorthData,

					netWorthDiff15:
						hasNetWorthData
							? scaledNetWorthDiff /
								100.0
							: 0,

					netWorthMatches:
						netWorthMatches,

					isBest:
						isBest
				});
			}

			if (stayCount !== 1) {
				throw new Error(
					'Lane Advisor must contain exactly one STAY.'
				);
			}

			/*
			 * bestCount=0 is allowed:
			 * insufficient-data.
			 */
			if (
				bestCount >
					1
			) {
				throw new Error(
					'Lane Advisor contains more than one BEST option.'
				);
			}

			return {
				status:
					'ready',

				localIndex:
					localIndex,

				options:
					options
			};
		};

	LaneAdvisorClient.prototype._validateMatches =
		function (matches) {
			if (
				!matches ||
				matches.length !==
					EXPECTED_PLAYERS
			) {
				return {
					error:
						this._createError(
							'invalid-match-count',

							'Lane Advisor expects exactly ' +
								EXPECTED_PLAYERS +
								' hero panels.',

							matches
								? matches.length
								: null
						),

					localIndex:
						-1
				};
			}

			var localIndex =
				-1;

			for (
				var index = 0;
				index < EXPECTED_PLAYERS;
				index++
			) {
				var match =
					matches[index];

				if (!match) {
					return {
						error:
							this._createError(
								'missing-roster-entry',

								'Roster entry is missing.',

								index
							),

						localIndex:
							-1
					};
				}

				if (
					match.rosterIndex !==
						index
				) {
					return {
						error:
							this._createError(
								'invalid-roster-order',

								'Ordered roster is invalid.',

								index
							),

						localIndex:
							-1
					};
				}

				if (
					!match.heroName ||
					!String(
						match.heroName
					).trim()
				) {
					return {
						error:
							this._createError(
								'missing-hero-name',

								'heroName is missing from the roster.',

								index
							),

						localIndex:
							-1
					};
				}

				if (match.isLocal) {
					if (localIndex !== -1) {
						return {
							error:
								this._createError(
									'multiple-local-players',

									'More than one local player was found.',

									null
								),

							localIndex:
								-1
						};
					}

					localIndex =
						index;
				}
			}

			if (
				localIndex < 0 ||
				localIndex >=
					ALLY_PLAYERS
			) {
				return {
					error:
						this._createError(
							'invalid-local-index',

							'The local player must be in ally indexes 0..5.',

							localIndex
						),

					localIndex:
						localIndex
				};
			}

			return {
				error:
					null,

				localIndex:
					localIndex
			};
		};

	LaneAdvisorClient.prototype._buildFingerprint =
		function (
			matches,
			localIndex
		) {
			var parts =
				[
					String(
						localIndex
					)
				];

			for (
				var index = 0;
				index < matches.length;
				index++
			) {
				parts.push(
					String(
						matches[index]
							.heroName ||
							''
					)
					.toLowerCase()
					.trim()
				);
			}

			return parts.join(
				'\u001f'
			);
		};

	LaneAdvisorClient.prototype._nextRosterVersion =
		function () {
			var now =
				Math.floor(
					Date.now()
				);

			if (
				now <=
					this._lastRosterVersion
			) {
				now =
					this._lastRosterVersion +
					1;
			}

			this._lastRosterVersion =
				now;

			return now;
		};

	LaneAdvisorClient.prototype._readUInt32LittleEndian =
		function (
			bytes,
			offset
		) {
			return (
				bytes[offset] +
				bytes[offset + 1] *
					256 +
				bytes[offset + 2] *
					65536 +
				bytes[offset + 3] *
					16777216
			);
		};

	LaneAdvisorClient.prototype._readInt32LittleEndian =
		function (
			bytes,
			offset
		) {
			var value =
				this._readUInt32LittleEndian(
					bytes,
					offset
				);

			if (
				value >=
					2147483648
			) {
				value -=
					4294967296;
			}

			return value;
		};

	LaneAdvisorClient.prototype._createError =
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

	LaneAdvisorClient.prototype._notifyResult =
		function (
			error,
			result,
			matches
		) {
			try {
				this._resultHandler(
					error || null,
					result || null,
					matches || null
				);
			} catch (
				handlerError
			) {
				this._log(
					'LaneAdvisorClient: RESULT HANDLER ERROR' +
						' | error=' +
							String(
								handlerError
							)
				);
			}
		};

	LaneAdvisorClient.prototype._invokeCallback =
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

	ThreatHud.LaneAdvisorClient =
		LaneAdvisorClient;

})(ThreatHud);
