using System.Text.Json;

internal sealed class MatchHistorySnapshot
{
    public int SchemaVersion
    {
        get;
        set;
    }

    public ulong MatchId
    {
        get;
        set;
    }

    public DateTimeOffset CapturedAtUtc
    {
        get;
        set;
    }

    public bool? LocalPlayerWon
    {
        get;
        set;
    }

    public uint? LocalPlayerAccountId
    {
        get;
        set;
    }

    public MatchHistoryContextSnapshot? Context
    {
        get;
        set;
    }

    public MatchHistoryModuleSnapshot? Modules
    {
        get;
        set;
    }

    public MatchHistoryServiceSnapshot? Services
    {
        get;
        set;
    }

    public DeadlockMatchPlayerDetailsSnapshot? PlayerDetails
    {
        get;
        set;
    }

    public MatchHistoryHeroDamageSnapshot? HeroDamage
    {
        get;
        set;
    }

    public MatchHistoryLaneStatsSnapshot? LaneStats
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryContextSnapshot
{
    public bool HeroDamageAllowedForMatch
    {
        get;
        set;
    }

    public DateTimeOffset? MatchObservedAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? HeroStatsGeneratedAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? HeroStatsReadyAtUtc
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryModuleSnapshot
{
    public bool Winrate
    {
        get;
        set;
    }

    public bool Rank
    {
        get;
        set;
    }

    public bool Adviser
    {
        get;
        set;
    }

    public bool HeroDamage
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryServiceSnapshot
{
    public string? Winrate
    {
        get;
        set;
    }

    public string? Rank
    {
        get;
        set;
    }

    public string? Adviser
    {
        get;
        set;
    }

    public string? HeroDamage
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryHeroDamageSnapshot
{
    public ulong MatchId
    {
        get;
        set;
    }

    public string? Status
    {
        get;
        set;
    }

    public DateTimeOffset? HeroStatsReadyAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? ScheduledStartAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? BroadcastReadyAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? StartedAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? ConnectedAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? LastEventAtUtc
    {
        get;
        set;
    }

    public DateTimeOffset? LastSampleAtUtc
    {
        get;
        set;
    }

    public string? StatusMessage
    {
        get;
        set;
    }

    public string? Error
    {
        get;
        set;
    }

    public string? Source
    {
        get;
        set;
    }

    public int? BroadcastProtocol
    {
        get;
        set;
    }

    public int? BroadcastTickRate
    {
        get;
        set;
    }

    public int? InitialFragment
    {
        get;
        set;
    }

    public long BroadcastStepCount
    {
        get;
        set;
    }

    public long PlayerSampleCount
    {
        get;
        set;
    }

    public long LastTick
    {
        get;
        set;
    }

    public List<MatchHistoryHeroDamagePlayer?>? Players
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryHeroDamagePlayer
{
    public uint AccountId
    {
        get;
        set;
    }

    public ulong SteamId64
    {
        get;
        set;
    }

    public uint HeroId
    {
        get;
        set;
    }

    public long HeroDamage
    {
        get;
        set;
    }

    public long Tick
    {
        get;
        set;
    }

    public DateTimeOffset UpdatedAtUtc
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryLaneStatsSnapshot
{
    public List<uint>? HeroIds
    {
        get;
        set;
    }

    public List<MatchHistoryLaneStatsEntry?>? Lanes
    {
        get;
        set;
    }
}

internal sealed class MatchHistoryLaneStatsEntry
{
    public int LaneIndex
    {
        get;
        set;
    }

    public bool HasMatchData
    {
        get;
        set;
    }

    public double? WinRatePercent
    {
        get;
        set;
    }

    public ulong Matches
    {
        get;
        set;
    }

    public bool HasNetWorthData
    {
        get;
        set;
    }

    public double? NetWorthDiff15
    {
        get;
        set;
    }

    public ulong NetWorthMatches
    {
        get;
        set;
    }
}

internal static class MatchHistorySnapshotReader
{
    private static readonly JsonSerializerOptions
        JsonOptions =
            new(
                JsonSerializerDefaults.Web
            )
            {
                PropertyNameCaseInsensitive =
                    true
            };

    public static MatchHistorySnapshot Read(
        MatchHistoryEntry entry
    )
    {
        ArgumentNullException.ThrowIfNull(
            entry
        );

        var snapshot =
            JsonSerializer.Deserialize<
                MatchHistorySnapshot
            >(
                entry.SnapshotJson,
                JsonOptions
            ) ??
            throw new InvalidDataException(
                "The saved match snapshot is empty."
            );

        if (snapshot.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "Unsupported match history schema version: " +
                snapshot.SchemaVersion +
                "."
            );
        }

        if (
            snapshot.MatchId == 0 ||
            snapshot.MatchId != entry.MatchId
        )
        {
            throw new InvalidDataException(
                "The saved snapshot does not match its match ID."
            );
        }

        if (
            snapshot.HeroDamage is not null &&
            snapshot.HeroDamage.MatchId !=
                snapshot.MatchId
        )
        {
            throw new InvalidDataException(
                "The saved Hero Damage belongs to another match."
            );
        }

        return snapshot;
    }
}
