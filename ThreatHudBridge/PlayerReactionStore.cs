using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

internal static class PlayerReactionValue
{
    public const int Dislike =
        -1;

    public const int None =
        0;

    public const int Like =
        1;

    public static bool IsValid(
        int reaction
    )
    {
        return
            reaction == Dislike ||
            reaction == None ||
            reaction == Like;
    }

    public static byte EncodeTransportByte(
        int reaction
    )
    {
        return reaction switch
        {
            None =>
                0,

            Like =>
                1,

            Dislike =>
                byte.MaxValue,

            _ =>
                throw new InvalidOperationException(
                    "Invalid reaction for transport: " +
                    reaction
                )
        };
    }
}

internal readonly record struct PlayerReactionListItem(
    uint AccountId,
    int Reaction,
    DateTimeOffset UpdatedAtUtc
);

internal sealed class PlayerReactionStore
{
    private const string LegacyTableName =
        "player_hero_reactions";

    private const string LegacyMigrationId =
        "player_reactions_from_player_hero_reactions_v1";

    /*
     * In the sandbox, 11 bots receive synthetic accountIds:
     *
     * botIndex 0..10
     * accountId 1..11
     *
     * These records are required for sandbox reaction tests,
     * so they are not removed from the DB and writes are not blocked.
     *
     * They are excluded only from the desktop list
     * of saved real players.
     */
    private const uint SandboxBotAccountIdMax =
        11;

    private readonly string _connectionString;

    public PlayerReactionStore()
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
                "player_reactions.db"
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

    /*
     * The database is stored outside the worker/exe.
     *
     * Terminating the worker process
     * does not delete player ratings.
     */
    public string DatabasePath
    {
        get;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken
    )
    {
        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await ExecuteNonQueryAsync(
            connection,
            transaction:
                null,
            sql:
                "PRAGMA journal_mode = WAL;",
            cancellationToken
        );

        await ExecuteNonQueryAsync(
            connection,
            transaction:
                null,
            sql:
                "PRAGMA busy_timeout = 5000;",
            cancellationToken
        );

        using var transaction =
            connection.BeginTransaction();

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS
                player_reactions
            (
                account_id INTEGER NOT NULL
                    PRIMARY KEY,

                reaction INTEGER NOT NULL
                    CHECK (reaction IN (-1, 1)),

                updated_at_utc TEXT NOT NULL
            );
            """,
            cancellationToken
        );

        /*
         * The marker table allows irreversible
         * data transformations to be performed
         * exactly once.
         *
         * Without the marker, a deleted player-only reaction
         * could be restored from the legacy table
         * after every worker restart.
         */
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS
                threat_hud_schema_migrations
            (
                migration_id TEXT NOT NULL
                    PRIMARY KEY,

                applied_at_utc TEXT NOT NULL
            );
            """,
            cancellationToken
        );

        var migrationApplied =
            await ExistsAsync(
                connection,
                transaction,
                """
                SELECT 1
                FROM threat_hud_schema_migrations
                WHERE migration_id = $migrationId
                LIMIT 1;
                """,
                cancellationToken,
                (
                    "$migrationId",
                    LegacyMigrationId
                )
            );

        if (!migrationApplied)
        {
            var legacyTableExists =
                await ExistsAsync(
                    connection,
                    transaction,
                    """
                    SELECT 1
                    FROM sqlite_master
                    WHERE
                        type = 'table'
                        AND
                        name = $tableName
                    LIMIT 1;
                    """,
                    cancellationToken,
                    (
                        "$tableName",
                        LegacyTableName
                    )
                );

            if (legacyTableExists)
            {
                /*
                 * The old model could store
                 * multiple reactions for one player
                 * on different heroes.
                 *
                 * The player-only model receives
                 * the most recent account ID record.
                 *
                 * rowid is used as a stable
                 * tie-breaker for identical timestamps.
                 */
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO player_reactions
                    (
                        account_id,
                        reaction,
                        updated_at_utc
                    )
                    SELECT
                        legacy.account_id,
                        legacy.reaction,
                        legacy.updated_at_utc
                    FROM player_hero_reactions
                        AS legacy
                    WHERE
                        legacy.reaction IN (-1, 1)
                        AND
                        legacy.rowid =
                        (
                            SELECT
                                candidate.rowid
                            FROM player_hero_reactions
                                AS candidate
                            WHERE
                                candidate.account_id =
                                    legacy.account_id
                            ORDER BY
                                candidate.updated_at_utc
                                    DESC,

                                candidate.rowid
                                    DESC
                            LIMIT 1
                        )
                    ON CONFLICT(account_id)
                    DO UPDATE SET
                        reaction =
                            excluded.reaction,

                        updated_at_utc =
                            excluded.updated_at_utc
                    WHERE
                        excluded.updated_at_utc >
                            player_reactions
                                .updated_at_utc;
                    """,
                    cancellationToken
                );
            }

            /*
             * The marker is created even if
             * the legacy table is absent.
             *
             * This marks completion of the migration
             * to the player-only model.
             */
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                INSERT INTO
                    threat_hud_schema_migrations
                (
                    migration_id,
                    applied_at_utc
                )
                VALUES
                (
                    $migrationId,
                    $appliedAtUtc
                )
                ON CONFLICT(migration_id)
                DO NOTHING;
                """,
                cancellationToken,
                (
                    "$migrationId",
                    LegacyMigrationId
                ),
                (
                    "$appliedAtUtc",
                    DateTimeOffset.UtcNow
                        .ToString("O")
                )
            );
        }

        await transaction.CommitAsync(
            cancellationToken
        );
    }

    /*
     * Returns:
     *
     * -1 = dislike
     *  0 = no reaction
     * +1 = like
     */
    public async Task<int> GetAsync(
        uint accountId,
        CancellationToken cancellationToken
    )
    {
        var reactions =
            await GetManyAsync(
                new[]
                {
                    accountId
                },
                cancellationToken
            );

        return reactions[
            accountId
        ];
    }

    /*
     * Reads reactions for multiple players
     * with a single SQLite query.
     *
     * For a missing account ID, the dictionary
     * contains reaction=0.
     */
    public async Task<
        IReadOnlyDictionary<uint, int>
    > GetManyAsync(
        IEnumerable<uint> accountIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            accountIds
        );

        var distinctAccountIds =
            accountIds
                .Distinct()
                .ToArray();

        var result =
            new Dictionary<uint, int>(
                distinctAccountIds.Length
            );

        for (
            var index = 0;
            index < distinctAccountIds.Length;
            index++
        )
        {
            var accountId =
                distinctAccountIds[index];

            ValidateAccountId(
                accountId
            );

            result[accountId] =
                PlayerReactionValue.None;
        }

        if (
            distinctAccountIds.Length ==
                0
        )
        {
            return result;
        }

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await using var command =
            connection.CreateCommand();

        var parameterNames =
            new string[
                distinctAccountIds.Length
            ];

        for (
            var index = 0;
            index < distinctAccountIds.Length;
            index++
        )
        {
            var parameterName =
                "$account" +
                index;

            parameterNames[index] =
                parameterName;

            command.Parameters.AddWithValue(
                parameterName,
                (long)distinctAccountIds[index]
            );
        }

        var sql =
            new StringBuilder();

        sql.AppendLine(
            "SELECT account_id, reaction"
        );

        sql.AppendLine(
            "FROM player_reactions"
        );

        sql.Append(
            "WHERE account_id IN ("
        );

        sql.Append(
            string.Join(
                ", ",
                parameterNames
            )
        );

        sql.AppendLine(
            ");"
        );

        command.CommandText =
            sql.ToString();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken
            );

        while (
            await reader.ReadAsync(
                cancellationToken
            )
        )
        {
            var accountId =
                ReadStoredAccountId(
                    reader,
                    0
                );

            var reaction =
                ReadStoredReaction(
                    reader,
                    1
                );

            result[accountId] =
                reaction;
        }

        return result;
    }

    /*
     * Returns one page of saved
     * reactions for real players.
     *
     * Synthetic sandbox accountId 1..11
     * are not included in the desktop list.
     *
     * New records come first.
     */
    public async Task<(
        IReadOnlyList<PlayerReactionListItem> Items,
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
                "pageSize must be greater than 0."
            );
        }

        var offset =
            checked(
                pageIndex *
                pageSize
            );

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await using var countCommand =
            CreateCommand(
                connection,
                transaction:
                    null,
                sql:
                    """
                    SELECT COUNT(*)
                    FROM player_reactions
                    WHERE
                        account_id >
                            $sandboxBotAccountIdMax;
                    """,
                (
                    "$sandboxBotAccountIdMax",
                    (long)SandboxBotAccountIdMax
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
            new List<PlayerReactionListItem>(
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
            CreateCommand(
                connection,
                transaction:
                    null,
                sql:
                    """
                    SELECT
                        account_id,
                        reaction,
                        updated_at_utc
                    FROM player_reactions
                    WHERE
                        account_id >
                            $sandboxBotAccountIdMax
                    ORDER BY
                        updated_at_utc DESC,
                        account_id DESC
                    LIMIT $pageSize
                    OFFSET $offset;
                    """,
                (
                    "$sandboxBotAccountIdMax",
                    (long)SandboxBotAccountIdMax
                ),
                (
                    "$pageSize",
                    pageSize
                ),
                (
                    "$offset",
                    offset
                )
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
            var accountId =
                ReadStoredAccountId(
                    reader,
                    0
                );

            var reaction =
                ReadStoredReaction(
                    reader,
                    1
                );

            var updatedAtText =
                reader.GetString(
                    2
                );

            if (
                !DateTimeOffset.TryParse(
                    updatedAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var updatedAtUtc
                )
            )
            {
                throw new InvalidOperationException(
                    "player_reactions contains " +
                    "an invalid updated_at_utc: " +
                    updatedAtText
                );
            }

            items.Add(
                new PlayerReactionListItem(
                    accountId,
                    reaction,
                    updatedAtUtc.ToUniversalTime()
                )
            );
        }

        return (
            items,
            totalCount
        );
    }

    /*
     * reaction:
     *
     * -1 → save dislike
     *  0 → delete the existing reaction
     * +1 → save like
     */
    public async Task SetAsync(
        uint accountId,
        int reaction,
        CancellationToken cancellationToken
    )
    {
        ValidateAccountId(
            accountId
        );

        if (
            !PlayerReactionValue.IsValid(
                reaction
            )
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(reaction),
                reaction,
                "Reaction must be -1, 0, or 1."
            );
        }

        if (
            reaction ==
                PlayerReactionValue.None
        )
        {
            await DeleteAsync(
                accountId,
                cancellationToken
            );

            return;
        }

        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await using var command =
            CreateCommand(
                connection,
                transaction:
                    null,
                sql:
                    """
                    INSERT INTO player_reactions
                    (
                        account_id,
                        reaction,
                        updated_at_utc
                    )
                    VALUES
                    (
                        $accountId,
                        $reaction,
                        $updatedAtUtc
                    )
                    ON CONFLICT(account_id)
                    DO UPDATE SET
                        reaction =
                            excluded.reaction,

                        updated_at_utc =
                            excluded.updated_at_utc;
                    """,
                (
                    "$accountId",
                    (long)accountId
                ),
                (
                    "$reaction",
                    reaction
                ),
                (
                    "$updatedAtUtc",
                    DateTimeOffset.UtcNow
                        .ToString("O")
                )
            );

        await command.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    private async Task DeleteAsync(
        uint accountId,
        CancellationToken cancellationToken
    )
    {
        await using var connection =
            CreateConnection();

        await connection.OpenAsync(
            cancellationToken
        );

        await using var command =
            CreateCommand(
                connection,
                transaction:
                    null,
                sql:
                    """
                    DELETE FROM player_reactions
                    WHERE account_id = $accountId;
                    """,
                (
                    "$accountId",
                    (long)accountId
                )
            );

        await command.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(
            _connectionString
        );
    }

    /*
     * Shared command initialization implementation.
     *
     * ExecuteNonQueryAsync() and ExistsAsync()
     * do not duplicate transaction setup,
     * SQL, and parameters.
     */
    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (
            string Name,
            object Value
        )[] parameters
    )
    {
        var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            sql;

        for (
            var index = 0;
            index < parameters.Length;
            index++
        )
        {
            var parameter =
                parameters[index];

            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value
            );
        }

        return command;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (
            string Name,
            object Value
        )[] parameters
    )
    {
        await using var command =
            CreateCommand(
                connection,
                transaction,
                sql,
                parameters
            );

        await command.ExecuteNonQueryAsync(
            cancellationToken
        );
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (
            string Name,
            object Value
        )[] parameters
    )
    {
        await using var command =
            CreateCommand(
                connection,
                transaction,
                sql,
                parameters
            );

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken
            );

        return
            value is not null &&
            value is not DBNull;
    }

    private static uint ReadStoredAccountId(
        SqliteDataReader reader,
        int ordinal
    )
    {
        var rawAccountId =
            reader.GetInt64(
                ordinal
            );

        if (
            rawAccountId <= 0 ||
            rawAccountId >
                uint.MaxValue
        )
        {
            throw new InvalidOperationException(
                "player_reactions contains " +
                "an invalid account_id: " +
                rawAccountId
            );
        }

        return (uint)rawAccountId;
    }

    private static int ReadStoredReaction(
        SqliteDataReader reader,
        int ordinal
    )
    {
        var reaction =
            reader.GetInt32(
                ordinal
            );

        ValidateStoredReaction(
            reaction
        );

        return reaction;
    }

    private static void ValidateAccountId(
        uint accountId
    )
    {
        if (accountId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accountId),
                "accountId cannot be 0."
            );
        }
    }

    private static void ValidateStoredReaction(
        int reaction
    )
    {
        if (
            reaction ==
                PlayerReactionValue.None ||
            !PlayerReactionValue.IsValid(
                reaction
            )
        )
        {
            throw new InvalidOperationException(
                "player_reactions contains " +
                "an invalid reaction value: " +
                reaction
            );
        }
    }
}