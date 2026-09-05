using System.Globalization;
using Microsoft.Data.Sqlite;

internal readonly record struct MatchHistoryListItem(
    ulong MatchId,
    DateTimeOffset AddedAtUtc,
    bool? LocalPlayerWon
);

internal sealed record MatchHistoryEntry(
    ulong MatchId,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string SnapshotJson
);

internal sealed class MatchHistoryStore
{
    internal const ulong MinimumPlausibleMatchId =
        10_000_000UL;

    private readonly string _connectionString;

    private readonly SemaphoreSlim _initializeGate =
        new(
            1,
            1
        );

    private volatile bool _initialized;

    public MatchHistoryStore()
    {
        var directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .LocalApplicationData
                ),
                "DeadlockThreatHud"
            );

        Directory.CreateDirectory(
            directory
        );

        DatabasePath =
            Path.Combine(
                directory,
                "match_history.db"
            );

        _connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    DatabasePath,

                Mode =
                    SqliteOpenMode.ReadWriteCreate
            }
            .ToString();
    }

    public string DatabasePath
    {
        get;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken
    )
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(
            cancellationToken
        );

        try
        {
            if (_initialized)
            {
                return;
            }

            const int maximumAttempts =
                4;

            for (
                var attempt = 1;
                attempt <= maximumAttempts;
                attempt++
            )
            {
                try
                {
                    await InitializeDatabaseAsync(
                        cancellationToken
                    );

                    break;
                }
                catch (SqliteException error) when (
                    IsBusyOrLocked(
                        error
                    ) &&
                    attempt < maximumAttempts
                )
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            100 * attempt
                        ),
                        cancellationToken
                    );
                }
            }

            _initialized =
                true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task UpsertSnapshotAsync(
        ulong matchId,
        DateTimeOffset capturedAtUtc,
        string snapshotJson,
        CancellationToken cancellationToken
    )
    {
        ValidateMatchId(
            matchId
        );

        if (
            String.IsNullOrWhiteSpace(
                snapshotJson
            )
        )
        {
            throw new ArgumentException(
                "Match history snapshot cannot be empty.",
                nameof(snapshotJson)
            );
        }

        var capturedAtText =
            capturedAtUtc
                .ToUniversalTime()
                .ToString(
                    "O",
                    CultureInfo.InvariantCulture
                );

        await InitializeAsync(
            cancellationToken
        );

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await ConfigureConnectionAsync(
            connection,
            cancellationToken
        );

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO match_history
            (
                match_id,
                added_at_utc,
                updated_at_utc,
                snapshot_json
            )
            VALUES
            (
                $matchId,
                $capturedAtUtc,
                $capturedAtUtc,
                $snapshotJson
            )
            ON CONFLICT(match_id)
            DO UPDATE SET
                updated_at_utc =
                    excluded.updated_at_utc,

                snapshot_json =
                    excluded.snapshot_json
            WHERE
                excluded.updated_at_utc >=
                    match_history.updated_at_utc;
            """;

        command.Parameters.AddWithValue(
            "$matchId",
            matchId.ToString(
                CultureInfo.InvariantCulture
            )
        );

        command.Parameters.AddWithValue(
            "$capturedAtUtc",
            capturedAtText
        );

        command.Parameters.AddWithValue(
            "$snapshotJson",
            snapshotJson
        );

        await command.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    public async Task<(
        IReadOnlyList<MatchHistoryListItem> Items,
        int TotalCount
    )> GetPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex),
                "pageIndex cannot be negative."
            );
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                "pageSize must be greater than zero."
            );
        }

        var offset =
            checked(
                pageIndex *
                pageSize
            );

        await InitializeAsync(
            cancellationToken
        );

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await ConfigureConnectionAsync(
            connection,
            cancellationToken
        );

        using var readTransaction =
            connection.BeginTransaction(
                deferred:
                    true
            );

        await using var countCommand =
            connection.CreateCommand();

        countCommand.Transaction =
            readTransaction;

        countCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM match_history
            WHERE
                length(match_id) >=
                    length($minimumMatchId)
                AND match_id NOT GLOB '*[^0-9]*';
            """;

        countCommand.Parameters.AddWithValue(
            "$minimumMatchId",
            MinimumPlausibleMatchId.ToString(
                CultureInfo.InvariantCulture
            )
        );

        var rawTotalCount =
            await countCommand.ExecuteScalarAsync(
                cancellationToken
            );

        var totalCount =
            Convert.ToInt32(
                rawTotalCount,
                CultureInfo.InvariantCulture
            );

        var items =
            new List<MatchHistoryListItem>(
                Math.Min(
                    pageSize,
                    totalCount
                )
            );

        if (totalCount == 0)
        {
            return (
                items,
                totalCount
            );
        }

        await using var pageCommand =
            connection.CreateCommand();

        pageCommand.Transaction =
            readTransaction;

        pageCommand.CommandText =
            """
            SELECT
                match_id,
                added_at_utc,
                CASE
                    WHEN json_valid(snapshot_json) = 0
                        THEN NULL
                    WHEN json_type(
                        snapshot_json,
                        '$.localPlayerWon'
                    ) = 'true'
                        THEN 1
                    WHEN json_type(
                        snapshot_json,
                        '$.localPlayerWon'
                    ) = 'false'
                        THEN 0
                    ELSE NULL
                END AS local_player_won
            FROM match_history
            WHERE
                length(match_id) >=
                    length($minimumMatchId)
                AND match_id NOT GLOB '*[^0-9]*'
            ORDER BY
                added_at_utc DESC,
                match_id DESC
            LIMIT $limit
            OFFSET $offset;
            """;

        pageCommand.Parameters.AddWithValue(
            "$minimumMatchId",
            MinimumPlausibleMatchId.ToString(
                CultureInfo.InvariantCulture
            )
        );

        pageCommand.Parameters.AddWithValue(
            "$limit",
            pageSize
        );

        pageCommand.Parameters.AddWithValue(
            "$offset",
            offset
        );

        await using var reader =
            await pageCommand.ExecuteReaderAsync(
                cancellationToken
            );

        while (
            await reader.ReadAsync(
                cancellationToken
            )
        )
        {
            var storedMatchId =
                ParseMatchId(
                    reader.GetString(
                        0
                    )
                );

            var storedAddedAtUtc =
                reader.GetString(
                    1
                );

            bool? localPlayerWon =
                reader.IsDBNull(
                    2
                )
                    ? null
                    : reader.GetInt64(
                        2
                    ) != 0;

            items.Add(
                new MatchHistoryListItem(
                    MatchId:
                        storedMatchId,

                    AddedAtUtc:
                        ParseUtcTimestamp(
                            storedAddedAtUtc,
                            "added_at_utc"
                        ),

                    LocalPlayerWon:
                        localPlayerWon
                )
            );
        }

        return (
            items,
            totalCount
        );
    }

    public async Task<MatchHistoryEntry?>
        GetSnapshotAsync(
            ulong matchId,
            CancellationToken cancellationToken
        )
    {
        ValidateMatchId(
            matchId
        );

        await InitializeAsync(
            cancellationToken
        );

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await ConfigureConnectionAsync(
            connection,
            cancellationToken
        );

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                match_id,
                added_at_utc,
                updated_at_utc,
                snapshot_json
            FROM match_history
            WHERE match_id = $matchId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$matchId",
            matchId.ToString(
                CultureInfo.InvariantCulture
            )
        );

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken
            );

        if (
            !await reader.ReadAsync(
                cancellationToken
            )
        )
        {
            return null;
        }

        return new MatchHistoryEntry(
            MatchId:
                ParseMatchId(
                    reader.GetString(
                        0
                    )
                ),

            AddedAtUtc:
                ParseUtcTimestamp(
                    reader.GetString(
                        1
                    ),
                    "added_at_utc"
                ),

            UpdatedAtUtc:
                ParseUtcTimestamp(
                    reader.GetString(
                        2
                    ),
                    "updated_at_utc"
                ),

            SnapshotJson:
                reader.GetString(
                    3
                )
        );
    }

    private static ulong ParseMatchId(
        string value
    )
    {
        if (
            !UInt64.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var matchId
            ) ||
            !IsPlausibleMatchId(
                matchId
            )
        )
        {
            throw new InvalidDataException(
                "Stored match history contains an invalid match ID."
            );
        }

        return matchId;
    }

    private static DateTimeOffset ParseUtcTimestamp(
        string value,
        string columnName
    )
    {
        if (
            !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp
            )
        )
        {
            throw new InvalidDataException(
                "Stored match history contains an invalid " +
                columnName +
                "."
            );
        }

        return timestamp.ToUniversalTime();
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(
            _connectionString
        );
    }

    private async Task InitializeDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await ConfigureConnectionAsync(
            connection,
            cancellationToken
        );

        await using (
            var journalModeCommand =
                connection.CreateCommand()
        )
        {
            journalModeCommand.CommandText =
                "PRAGMA journal_mode = WAL;";

            var rawJournalMode =
                await journalModeCommand.ExecuteScalarAsync(
                    cancellationToken
                );

            var journalMode =
                Convert.ToString(
                    rawJournalMode,
                    CultureInfo.InvariantCulture
                );

            if (
                !String.Equals(
                    journalMode,
                    "wal",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new InvalidOperationException(
                    "Match history database did not enable WAL mode."
                );
            }
        }

        await using var schemaCommand =
            connection.CreateCommand();

        schemaCommand.CommandText =
            """
            CREATE TABLE IF NOT EXISTS
                match_history
            (
                match_id TEXT NOT NULL
                    PRIMARY KEY,

                added_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
                    CHECK (length(snapshot_json) > 0)
            );

            CREATE INDEX IF NOT EXISTS
                idx_match_history_added_at_utc
            ON match_history
                (
                    added_at_utc DESC,
                    match_id DESC
                );
            """;

        await schemaCommand.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    private static bool IsBusyOrLocked(
        SqliteException error
    )
    {
        return
            error.SqliteErrorCode == 5 ||
            error.SqliteErrorCode == 6;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command =
            connection.CreateCommand();

        command.CommandText =
            "PRAGMA busy_timeout = 5000;";

        await command.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    private static void ValidateMatchId(
        ulong matchId
    )
    {
        if (
            !IsPlausibleMatchId(
                matchId
            )
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchId),
                "matchId must contain at least 8 decimal digits."
            );
        }
    }

    internal static bool IsPlausibleMatchId(
        ulong matchId
    )
    {
        return matchId >=
            MinimumPlausibleMatchId;
    }
}
