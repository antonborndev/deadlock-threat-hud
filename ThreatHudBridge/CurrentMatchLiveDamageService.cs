using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class CurrentMatchLiveDamageService : IAsyncDisposable
{
    private const string ParserPathVariable = "THREATHUD_BROADCAST_PARSER_PATH";
    private const string ParserResourceName =
        "ThreatHudBridge.Resources.ThreatHudBroadcastParser.exe";

    private static readonly TimeSpan SnapshotLogInterval = TimeSpan.FromSeconds(30);
    private static readonly object ParserExtractionGate = new();
    private static string? _cachedEmbeddedParserPath;
    private static byte[]? _cachedEmbeddedParserHash;

    private readonly Action<string> _log;
    private readonly object _stateGate = new();
    private readonly Dictionary<uint, CurrentMatchLiveDamagePlayer> _players = new();

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private long _generation;
    private ulong? _disabledMatchId;
    private DateTimeOffset? _lastSnapshotLogAtUtc;
    private CurrentMatchLiveDamageSnapshot _snapshot = CurrentMatchLiveDamageSnapshot.Waiting;

    public CurrentMatchLiveDamageService(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public void ResetMatch(ulong matchId)
    {
        CancellationTokenSource? previousCancellation;
        Task? previousTask;

        lock (_stateGate)
        {
            if (_snapshot.MatchId == matchId)
            {
                return;
            }

            previousCancellation = _runCancellation;
            previousTask = _runTask;
            _runCancellation = null;
            _runTask = null;
            _generation++;
            _disabledMatchId = null;
            _players.Clear();
            _lastSnapshotLogAtUtc = null;
            _snapshot = matchId == 0
                ? CurrentMatchLiveDamageSnapshot.Waiting
                : CurrentMatchLiveDamageSnapshot.ForMatch(matchId);
        }

        Cancel(previousCancellation);
        _ = DisposeCompletedRunAsync(previousTask, previousCancellation);
    }

    public bool DisableForMatch(ulong matchId)
    {
        CancellationTokenSource? previousCancellation;
        Task? previousTask;

        lock (_stateGate)
        {
            if (
                matchId == 0 ||
                _snapshot.MatchId != matchId ||
                _disabledMatchId == matchId
            )
            {
                return false;
            }

            _disabledMatchId = matchId;
            _generation++;
            previousCancellation = _runCancellation;
            previousTask = _runTask;
            _runCancellation = null;
            _runTask = null;
            _players.Clear();
            _lastSnapshotLogAtUtc = null;
            _snapshot = CurrentMatchLiveDamageSnapshot.ForMatch(matchId) with
            {
                Status = "disabled",
                LastEventAtUtc = DateTimeOffset.UtcNow,
                StatusMessage = "Hero Damage is disabled for this match"
            };
        }

        Cancel(previousCancellation);
        _ = DisposeCompletedRunAsync(previousTask, previousCancellation);
        return true;
    }

    public bool MarkScheduled(
        ulong matchId,
        DateTimeOffset heroStatsReadyAtUtc,
        DateTimeOffset scheduledStartAtUtc)
    {
        lock (_stateGate)
        {
            if (
                matchId == 0 ||
                _snapshot.MatchId != matchId ||
                _disabledMatchId == matchId ||
                _runTask is not null
            )
            {
                return false;
            }

            _snapshot = _snapshot with
            {
                Status = "waiting-for-broadcast",
                HeroStatsReadyAtUtc = heroStatsReadyAtUtc,
                ScheduledStartAtUtc = scheduledStartAtUtc,
                StatusMessage = "Waiting for broadcast URL",
                Error = null
            };
            return true;
        }
    }

    public bool StartForBroadcast(
        ulong matchId,
        string broadcastUrl,
        DateTimeOffset broadcastReadyAtUtc)
    {
        var normalizedUrl = NormalizeBroadcastUrl(broadcastUrl);
        CancellationTokenSource cancellation;
        long generation;

        lock (_stateGate)
        {
            if (
                matchId == 0 ||
                _snapshot.MatchId != matchId ||
                _disabledMatchId == matchId ||
                _runTask is not null
            )
            {
                return false;
            }

            generation = _generation;
            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            var now = DateTimeOffset.UtcNow;

            _snapshot = _snapshot with
            {
                Status = "starting-parser",
                BroadcastReadyAtUtc = broadcastReadyAtUtc,
                StartedAtUtc = now,
                LastEventAtUtc = now,
                StatusMessage = "Starting dynamic broadcast parser sidecar",
                Error = null,
                Source = "dynamic-haste-sidecar",
                BroadcastUrl = normalizedUrl
            };

            _runTask = RunAsync(matchId, generation, normalizedUrl, cancellation.Token);
        }

        return true;
    }

    public CurrentMatchLiveDamageSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            return _snapshot with
            {
                Players = _players.Values.OrderBy(player => player.AccountId).ToArray()
            };
        }
    }

    private async Task RunAsync(
        ulong matchId,
        long generation,
        string broadcastUrl,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        CancellationTokenRegistration registration = default;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parserPath = ResolveParserPath();

            cancellationToken.ThrowIfCancellationRequested();

            process = new Process
            {
                StartInfo = CreateStartInfo(parserPath, matchId, broadcastUrl),
                EnableRaisingEvents = true
            };

            _log(
                "Current match live damage: starting broadcast parser sidecar" +
                " | matchId=" + matchId +
                " | parser=" + parserPath +
                " | url=" + broadcastUrl);

            cancellationToken.ThrowIfCancellationRequested();

            if (!process.Start())
            {
                throw new InvalidOperationException("ThreatHudBroadcastParser process did not start.");
            }

            registration = cancellationToken.Register(
                static state =>
                {
                    if (state is Process running)
                    {
                        TryKill(running);
                    }
                },
                process);

            MarkStatus(matchId, generation, "parser-running", "Parser process started | pid=" + process.Id);

            var protocol = new ProtocolState();
            var stdoutTask = ReadStdoutAsync(
                matchId,
                generation,
                process.StandardOutput,
                protocol,
                cancellationToken);
            var stderrTask = ReadStderrAsync(
                matchId,
                process.StandardError,
                protocol,
                cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);

            try
            {
                var firstCompleted = await Task.WhenAny(
                    exitTask,
                    stdoutTask,
                    stderrTask);

                if (!ReferenceEquals(firstCompleted, exitTask) && firstCompleted.IsFaulted)
                {
                    TryKill(process);
                }

                await Task.WhenAll(
                    exitTask,
                    stdoutTask,
                    stderrTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsCurrentRunThreadSafe(matchId, generation))
            {
                return;
            }

            var state = protocol.Snapshot();
            if (state.Error is not null)
            {
                throw new InvalidOperationException(
                    "ThreatHudBroadcastParser reported an error: " + state.Error);
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "ThreatHudBroadcastParser exited with code " + process.ExitCode +
                    ": " + (state.LastStderr ?? "no stderr output"));
            }
            if (!state.Ready)
            {
                throw new InvalidOperationException(
                    "ThreatHudBroadcastParser exited without a ready event.");
            }

            MarkEnded(matchId, generation);
            _log(
                "Current match live damage: broadcast parser sidecar ended" +
                " | matchId=" + matchId +
                " | exitCode=" + process.ExitCode +
                " | endEvent=" + state.End);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            MarkError(matchId, generation, error.Message);
            _log("Current match live damage ERROR | matchId=" + matchId + " | " + error);
        }
        finally
        {
            registration.Dispose();
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private async Task ReadStdoutAsync(
        ulong matchId,
        long generation,
        StreamReader reader,
        ProtocolState protocol,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }
            if (!String.IsNullOrWhiteSpace(line))
            {
                HandleProtocolLine(matchId, generation, line, protocol);
            }
        }
    }

    private async Task ReadStderrAsync(
        ulong matchId,
        StreamReader reader,
        ProtocolState protocol,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }
            if (String.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            protocol.SetStderr(line);
            _log("Current match live damage: parser stderr | matchId=" + matchId + " | " + line);
        }
    }

    private void HandleProtocolLine(
        ulong matchId,
        long generation,
        string line,
        ProtocolState protocol)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = GetString(root, "type");
        var eventMatchId = GetUInt64(root, "match_id");

        if (eventMatchId != matchId)
        {
            throw new InvalidDataException(
                "Parser emitted matchId=" + eventMatchId + ", expected=" + matchId + ".");
        }

        switch (type)
        {
            case "ready":
                protocol.MarkReady();
                MarkConnected(matchId, generation);
                _log(
                    "Current match live damage: broadcast parser READY" +
                    " | matchId=" + matchId +
                    " | parserVersion=" + GetOptionalString(root, "parser_version") +
                    " | relayAttempts=" + GetOptionalInt64Text(root, "relay_attempts") +
                    " | bootstrapMs=" + GetOptionalInt64Text(root, "bootstrap_duration_ms") +
                    " | " + FormatTraffic(root));
                break;

            case "relay_wait":
                var attempt = GetInt32(root, "attempt");
                var retryDelayMs = GetInt32(root, "retry_delay_ms");
                var reason = GetString(root, "reason");
                MarkStatus(
                    matchId,
                    generation,
                    "waiting-for-broadcast-relay",
                    "Valve relay not ready; retry attempt " +
                    (attempt + 1).ToString(CultureInfo.InvariantCulture));
                _log(
                    "Current match live damage: relay bootstrap waiting" +
                    " | matchId=" + matchId +
                    " | attempt=" + attempt +
                    " | retryDelayMs=" + retryDelayMs +
                    " | reason=" + reason +
                    " | " + FormatTraffic(root));
                break;

            case "player_damage":
                HandlePlayerEvent(matchId, generation, root);
                break;

            case "heartbeat":
                HandleHeartbeat(matchId, generation, root);
                break;

            case "end":
                protocol.MarkEnd();
                Touch(matchId, generation, 0, "Broadcast parser reported stream end");
                _log(
                    "Current match live damage: relay traffic final" +
                    " | matchId=" + matchId +
                    " | " + FormatTraffic(root));
                break;

            case "error":
                protocol.SetError(GetString(root, "message"));
                break;

            default:
                throw new InvalidDataException("Unknown parser event type: " + type);
        }
    }

    private void HandlePlayerEvent(
        ulong matchId,
        long generation,
        JsonElement root)
    {
        var tick = GetInt32(root, "tick");
        var steamId64 = GetUInt64(root, "steam_id64");
        var accountId = checked((uint)GetUInt64(root, "account_id"));
        var heroId = checked((uint)GetUInt64(root, "hero_id"));
        var damage = Math.Max(0, GetInt32(root, "hero_damage"));

        if (steamId64 == 0 || accountId == 0 || heroId == 0)
        {
            return;
        }
        if (accountId != unchecked((uint)(steamId64 & 0xFFFFFFFFUL)))
        {
            throw new InvalidDataException("Parser accountId does not match SteamID64.");
        }

        var player = new CurrentMatchLiveDamagePlayer(
            accountId,
            steamId64,
            heroId,
            damage,
            tick,
            DateTimeOffset.UtcNow);

        var discovered = false;
        CurrentMatchLiveDamageSnapshot? snapshotForLog;

        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            var exists = _players.TryGetValue(accountId, out var previous);
            if (exists && previous is not null && tick < previous.Tick)
            {
                return;
            }

            discovered = !exists;
            _players[accountId] = player;
            var now = DateTimeOffset.UtcNow;
            _snapshot = _snapshot with
            {
                Status = "streaming",
                LastEventAtUtc = now,
                LastSampleAtUtc = now,
                StatusMessage = "Receiving live player-controller damage",
                Error = null,
                BroadcastStepCount = _snapshot.BroadcastStepCount + 1,
                PlayerSampleCount = _snapshot.PlayerSampleCount + 1,
                LastTick = Math.Max(_snapshot.LastTick, tick)
            };
            snapshotForLog = BuildPeriodicSnapshot(now);
        }

        if (discovered)
        {
            _log(
                "Current match live damage: player discovered" +
                " | matchId=" + matchId +
                " | accountId=" + accountId +
                " | steamId64=" + steamId64 +
                " | heroId=" + heroId +
                " | heroDamage=" + damage +
                " | tick=" + tick);
        }

        LogSnapshot(matchId, snapshotForLog);
    }

    private void HandleHeartbeat(
        ulong matchId,
        long generation,
        JsonElement root)
    {
        var tick = GetInt32(root, "tick");
        var trackedPlayers = GetInt32(root, "tracked_players");
        CurrentMatchLiveDamageSnapshot? snapshotForLog;

        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _snapshot = _snapshot with
            {
                Status = "streaming",
                LastEventAtUtc = now,
                StatusMessage = "Dynamic parser alive | trackedPlayers=" + trackedPlayers,
                Error = null,
                BroadcastStepCount = _snapshot.BroadcastStepCount + 1,
                LastTick = Math.Max(_snapshot.LastTick, tick)
            };
            snapshotForLog = BuildPeriodicSnapshot(now);
        }

        LogSnapshot(matchId, snapshotForLog);

        _log(
            "Current match live damage: relay traffic" +
            " | matchId=" + matchId +
            " | tick=" + tick +
            " | " + FormatTraffic(root));
    }

    private CurrentMatchLiveDamageSnapshot? BuildPeriodicSnapshot(DateTimeOffset now)
    {
        if (_lastSnapshotLogAtUtc.HasValue &&
            now - _lastSnapshotLogAtUtc.Value < SnapshotLogInterval)
        {
            return null;
        }

        _lastSnapshotLogAtUtc = now;
        return _snapshot with
        {
            Players = _players.Values.OrderBy(player => player.AccountId).ToArray()
        };
    }

    private void LogSnapshot(ulong matchId, CurrentMatchLiveDamageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var values = snapshot.Players.Count == 0
            ? "—"
            : String.Join(
                ",",
                snapshot.Players.Select(player =>
                    player.AccountId.ToString(CultureInfo.InvariantCulture) +
                    "/" + player.HeroId.ToString(CultureInfo.InvariantCulture) +
                    ":" + player.HeroDamage.ToString(CultureInfo.InvariantCulture)));

        _log(
            "Current match live damage: snapshot" +
            " | matchId=" + matchId +
            " | events=" + snapshot.BroadcastStepCount +
            " | samples=" + snapshot.PlayerSampleCount +
            " | players=" + snapshot.Players.Count +
            " | lastTick=" + snapshot.LastTick +
            " | values=" + values);
    }

    private void MarkConnected(ulong matchId, long generation)
    {
        lock (_stateGate)
        {
            if (!IsCurrentRun(matchId, generation))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _lastSnapshotLogAtUtc = null;
            _snapshot = _snapshot with
            {
                Status = "streaming",
                ConnectedAtUtc = now,
                LastEventAtUtc = now,
                StatusMessage = "Dynamic broadcast parser connected",
                Error = null,
                BroadcastStepCount = _snapshot.BroadcastStepCount + 1
            };
        }
    }

    private void MarkStatus(ulong matchId, long generation, string status, string message)
    {
        lock (_stateGate)
        {
            if (IsCurrentRun(matchId, generation))
            {
                _snapshot = _snapshot with
                {
                    Status = status,
                    LastEventAtUtc = DateTimeOffset.UtcNow,
                    StatusMessage = message,
                    Error = null
                };
            }
        }
    }

    private void Touch(ulong matchId, long generation, int tick, string message)
    {
        lock (_stateGate)
        {
            if (IsCurrentRun(matchId, generation))
            {
                _snapshot = _snapshot with
                {
                    LastEventAtUtc = DateTimeOffset.UtcNow,
                    StatusMessage = message,
                    BroadcastStepCount = _snapshot.BroadcastStepCount + 1,
                    LastTick = Math.Max(_snapshot.LastTick, tick)
                };
            }
        }
    }

    private void MarkEnded(ulong matchId, long generation)
    {
        lock (_stateGate)
        {
            if (IsCurrentRun(matchId, generation))
            {
                _snapshot = _snapshot with
                {
                    Status = "ended",
                    LastEventAtUtc = DateTimeOffset.UtcNow,
                    StatusMessage = "Broadcast parser ended",
                    Error = null
                };
            }
        }
    }

    private void MarkError(ulong matchId, long generation, string error)
    {
        lock (_stateGate)
        {
            if (IsCurrentRun(matchId, generation))
            {
                _snapshot = _snapshot with
                {
                    Status = "error",
                    LastEventAtUtc = DateTimeOffset.UtcNow,
                    StatusMessage = "Dynamic broadcast parser sidecar failed",
                    Error = error
                };
            }
        }
    }

    private bool IsCurrentRun(ulong matchId, long generation) =>
        _generation == generation && _snapshot.MatchId == matchId;

    private bool IsCurrentRunThreadSafe(ulong matchId, long generation)
    {
        lock (_stateGate)
        {
            return IsCurrentRun(matchId, generation);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string parserPath,
        ulong matchId,
        string broadcastUrl)
    {
        var info = new ProcessStartInfo
        {
            FileName = parserPath,
            WorkingDirectory = Path.GetDirectoryName(parserPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--match-id");
        info.ArgumentList.Add(matchId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--broadcast-url");
        info.ArgumentList.Add(broadcastUrl);
        return info;
    }

    private static string ResolveParserPath()
    {
        var configured = Environment.GetEnvironmentVariable(ParserPathVariable);
        if (!String.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured.Trim());
            if (!File.Exists(configuredPath))
            {
                throw new FileNotFoundException(
                    ParserPathVariable + " points to a missing parser executable.",
                    configuredPath);
            }

            return configuredPath;
        }

        lock (ParserExtractionGate)
        {
            var assembly = typeof(CurrentMatchLiveDamageService).Assembly;
            var expectedHash =
                _cachedEmbeddedParserHash ??=
                    ComputeEmbeddedParserHash(assembly);

            if (
                !String.IsNullOrWhiteSpace(_cachedEmbeddedParserPath) &&
                FileHashMatches(_cachedEmbeddedParserPath, expectedHash))
            {
                return _cachedEmbeddedParserPath;
            }

            var hashText = Convert.ToHexString(expectedHash).ToLowerInvariant();

            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            if (String.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            var parserDirectory = Path.Combine(
                localAppData,
                "DeadlockThreatHUD",
                "Runtime",
                "BroadcastParser");

            Directory.CreateDirectory(parserDirectory);

            var parserPath = Path.Combine(
                parserDirectory,
                "ThreatHudBroadcastParser-" + hashText[..16] + ".exe");

            if (!FileHashMatches(parserPath, expectedHash))
            {
                ExtractEmbeddedParser(
                    assembly,
                    parserPath,
                    expectedHash);
            }

            _cachedEmbeddedParserPath = parserPath;
            TryDeleteOldEmbeddedParsers(parserDirectory, parserPath);
            return parserPath;
        }
    }

    private static byte[] ComputeEmbeddedParserHash(Assembly assembly)
    {
        using var stream = OpenEmbeddedParserStream(assembly);
        return SHA256.HashData(stream);
    }

    private static Stream OpenEmbeddedParserStream(Assembly assembly)
    {
        return assembly.GetManifestResourceStream(ParserResourceName) ??
            throw new InvalidOperationException(
                "Embedded parser resource was not found: " +
                ParserResourceName);
    }

    private static void ExtractEmbeddedParser(
        Assembly assembly,
        string parserPath,
        byte[] expectedHash)
    {
        var temporaryPath =
            parserPath +
            "." +
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var source = OpenEmbeddedParserStream(assembly))
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                options: FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            if (!FileHashMatches(temporaryPath, expectedHash))
            {
                throw new InvalidDataException(
                    "Extracted parser failed SHA-256 verification.");
            }

            try
            {
                File.Move(
                    temporaryPath,
                    parserPath,
                    overwrite: true);
            }
            catch (IOException)
            when (FileHashMatches(parserPath, expectedHash))
            {
                // Another Bridge process completed the same extraction first.
            }
            catch (UnauthorizedAccessException)
            when (FileHashMatches(parserPath, expectedHash))
            {
                // The verified target may already be executing in another process.
            }

            if (!FileHashMatches(parserPath, expectedHash))
            {
                throw new InvalidDataException(
                    "Embedded parser target failed SHA-256 verification.");
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool FileHashMatches(
        string path,
        byte[] expectedHash)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);

            var actualHash = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteOldEmbeddedParsers(
        string parserDirectory,
        string currentParserPath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                parserDirectory,
                "ThreatHudBroadcastParser-*.exe",
                SearchOption.TopDirectoryOnly))
            {
                if (String.Equals(
                    path,
                    currentParserPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeBroadcastUrl(string broadcastUrl)
    {
        if (String.IsNullOrWhiteSpace(broadcastUrl) ||
            !Uri.TryCreate(broadcastUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "broadcastUrl must be an absolute HTTP or HTTPS URL.",
                nameof(broadcastUrl));
        }
        return uri.ToString().TrimEnd('/');
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Missing JSON string property: " + name);
        }
        return value.GetString() ?? String.Empty;
    }

    private static string GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "—"
            : "—";

    private static string GetOptionalInt64Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result.ToString(CultureInfo.InvariantCulture)
            : "—";

    private static string FormatTraffic(JsonElement root)
    {
        if (
            !root.TryGetProperty("traffic", out var traffic) ||
            traffic.ValueKind != JsonValueKind.Object)
        {
            return "relayRequests=—";
        }

        return
            "relayRequests=" + GetOptionalInt64Text(traffic, "requests_total") +
            " | sync=" + GetOptionalInt64Text(traffic, "sync_requests") +
            " | start=" + GetOptionalInt64Text(traffic, "start_requests") +
            " | full=" + GetOptionalInt64Text(traffic, "full_requests") +
            " | delta=" + GetOptionalInt64Text(traffic, "delta_requests") +
            " | http2xx=" + GetOptionalInt64Text(traffic, "responses_2xx") +
            " | http404=" + GetOptionalInt64Text(traffic, "responses_404") +
            " | http405=" + GetOptionalInt64Text(traffic, "responses_405") +
            " | httpOther=" + GetOptionalInt64Text(traffic, "responses_other") +
            " | transportErrors=" + GetOptionalInt64Text(traffic, "transport_errors") +
            " | bodyErrors=" + GetOptionalInt64Text(traffic, "body_errors") +
            " | decodedBytes=" + GetOptionalInt64Text(traffic, "decoded_body_bytes");
    }

    private static ulong GetUInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetUInt64(out var result))
        {
            throw new InvalidDataException("Missing JSON uint64 property: " + name);
        }
        return result;
    }

    private static int GetInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException("Missing JSON int32 property: " + name);
        }
        return result;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task DisposeCompletedRunAsync(
        Task? task,
        CancellationTokenSource? cancellation)
    {
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch { }
        }
        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_stateGate)
        {
            _generation++;
            task = _runTask;
            cancellation = _runCancellation;
            _runTask = null;
            _runCancellation = null;
            _disabledMatchId = null;
            _players.Clear();
            _snapshot = CurrentMatchLiveDamageSnapshot.Waiting;
        }

        Cancel(cancellation);
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch { }
        }
        cancellation?.Dispose();
    }

    private sealed class ProtocolState
    {
        private readonly object _gate = new();
        private bool _ready;
        private bool _end;
        private string? _error;
        private string? _lastStderr;

        public void MarkReady() { lock (_gate) { _ready = true; } }
        public void MarkEnd() { lock (_gate) { _end = true; } }
        public void SetError(string value) { lock (_gate) { _error = value; } }
        public void SetStderr(string value) { lock (_gate) { _lastStderr = value; } }
        public ProtocolSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new ProtocolSnapshot(_ready, _end, _error, _lastStderr);
            }
        }
    }

    private sealed record ProtocolSnapshot(
        bool Ready,
        bool End,
        string? Error,
        string? LastStderr);
}

internal sealed record CurrentMatchLiveDamagePlayer(
    uint AccountId,
    ulong SteamId64,
    uint HeroId,
    long HeroDamage,
    long Tick,
    DateTimeOffset UpdatedAtUtc);

internal sealed record CurrentMatchLiveDamageSnapshot(
    ulong MatchId,
    string Status,
    DateTimeOffset? HeroStatsReadyAtUtc,
    DateTimeOffset? ScheduledStartAtUtc,
    DateTimeOffset? BroadcastReadyAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastEventAtUtc,
    DateTimeOffset? LastSampleAtUtc,
    string? StatusMessage,
    string? Error,
    string? Source,
    string? BroadcastUrl,
    int? BroadcastProtocol,
    int? BroadcastTickRate,
    int? InitialFragment,
    long BroadcastStepCount,
    long PlayerSampleCount,
    long LastTick,
    IReadOnlyList<CurrentMatchLiveDamagePlayer> Players)
{
    public static CurrentMatchLiveDamageSnapshot Waiting => new(
        0,
        "waiting",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        "Waiting for current match ID",
        null,
        null,
        null,
        null,
        null,
        null,
        0,
        0,
        0,
        Array.Empty<CurrentMatchLiveDamagePlayer>());

    public static CurrentMatchLiveDamageSnapshot ForMatch(ulong matchId) => new(
        matchId,
        "waiting-for-hero-stats",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        "Waiting for CURRENT HERO STATS",
        null,
        null,
        null,
        null,
        null,
        null,
        0,
        0,
        0,
        Array.Empty<CurrentMatchLiveDamagePlayer>());
}
