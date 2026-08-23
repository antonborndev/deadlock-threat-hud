using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DeadlockApiClient
{
    private const int MaximumAccountIds =
        1000;

    private static readonly JsonSerializerOptions
        JsonOptions =
            new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive =
                    true
            };

    private readonly HttpClient _httpClient;

    public DeadlockApiClient(
        HttpClient httpClient
    )
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(
                nameof(httpClient)
            );
    }

    public async Task<
        IReadOnlyList<DeadlockHeroStats>
    > GetHeroStatsAsync(
        IEnumerable<uint> accountIds,
        IEnumerable<uint>? heroIds,
        CancellationToken cancellationToken
    )
    {
        var accountIdArray =
            accountIds
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray();

        if (
            accountIdArray.Length == 0
        )
        {
            return
                Array.Empty<
                    DeadlockHeroStats
                >();
        }

        if (
            accountIdArray.Length >
            MaximumAccountIds
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(accountIds),

                "Deadlock API accepts a maximum of " +
                $"{MaximumAccountIds} account ID " +
                "per request."
            );
        }

        var heroIdArray =
            heroIds?
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray() ??
            Array.Empty<uint>();

        var queryParts =
            new List<string>
            {
                "account_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        accountIdArray
                    )
                ),

                "game_mode=normal",

                "match_mode=" +
                Uri.EscapeDataString(
                    "ranked,unranked"
                )
            };

        if (heroIdArray.Length > 0)
        {
            queryParts.Add(
                "hero_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        heroIdArray
                    )
                )
            );
        }

        var requestUri =
            "v1/players/hero-stats?" +
            string.Join(
                "&",
                queryParts
            );

        return await GetJsonAsync<
            List<DeadlockHeroStats>
        >(
            requestUri,
            cancellationToken
        );
    }

    public async Task<
        IReadOnlyList<DeadlockLaneMatchupStats>
    > GetLaneMatchupStatsAsync(
        IEnumerable<uint> heroIds,
        IEnumerable<uint> enemyHeroIds,
        long minMatches,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            heroIds
        );

        ArgumentNullException.ThrowIfNull(
            enemyHeroIds
        );

        var heroIdArray =
            heroIds
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray();

        var enemyHeroIdArray =
            enemyHeroIds
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray();

        if (heroIdArray.Length < 2)
        {
            throw new ArgumentException(
                "Lane matchup requires at least " +
                "two allied hero IDs.",
                nameof(heroIds)
            );
        }

        if (enemyHeroIdArray.Length < 2)
        {
            throw new ArgumentException(
                "Lane matchup requires at least " +
                "two enemy hero IDs.",
                nameof(enemyHeroIds)
            );
        }

        if (minMatches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minMatches),
                "minMatches must be at least 1."
            );
        }

        var queryParts =
            new[]
            {
                "game_mode=normal",

                "match_mode=" +
                Uri.EscapeDataString(
                    "ranked,unranked"
                ),

                "hero_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        heroIdArray
                    )
                ),

                "enemy_hero_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        enemyHeroIdArray
                    )
                ),

                /*
                 * Advisor evaluates souls
                 * specifically at the 15:00 mark.
                 */
                "sample_time_s=900",

                "min_matches=" +
                minMatches
            };

        var requestUri =
            "v1/analytics/lane-matchup-stats?" +
            string.Join(
                "&",
                queryParts
            );

        return await GetJsonAsync<
            List<DeadlockLaneMatchupStats>
        >(
            requestUri,
            cancellationToken
        );
    }

    public async Task<
        IReadOnlyList<DeadlockHeroCombStats>
    > GetHeroCombinationStatsAsync(
        IEnumerable<uint> heroIds,
        IEnumerable<uint> enemyHeroIds,
        long minMatches,
        int combSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            heroIds
        );

        ArgumentNullException.ThrowIfNull(
            enemyHeroIds
        );

        var heroIdArray =
            heroIds
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray();

        var enemyHeroIdArray =
            enemyHeroIds
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray();

        if (
            combSize < 2 ||
            combSize > 6
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(combSize),
                "combSize must be from 2 to 6."
            );
        }

        if (
            heroIdArray.Length !=
                combSize
        )
        {
            throw new ArgumentException(
                "Hero combination requires exactly " +
                combSize +
                " allied hero IDs.",
                nameof(heroIds)
            );
        }

        if (
            enemyHeroIdArray.Length !=
                combSize
        )
        {
            throw new ArgumentException(
                "Hero combination requires exactly " +
                combSize +
                " enemy hero IDs.",
                nameof(enemyHeroIds)
            );
        }

        if (minMatches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minMatches),
                "minMatches must be at least 1."
            );
        }

        var queryParts =
            new[]
            {
                "game_mode=normal",

                "match_mode=" +
                Uri.EscapeDataString(
                    "ranked,unranked"
                ),

                "include_hero_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        heroIdArray
                    )
                ),

                "include_enemy_hero_ids=" +
                Uri.EscapeDataString(
                    string.Join(
                        ",",
                        enemyHeroIdArray
                    )
                ),

                "min_matches=" +
                minMatches,

                "comb_size=" +
                combSize
            };

        var requestUri =
            "v1/analytics/hero-comb-stats?" +
            string.Join(
                "&",
                queryParts
            );

        return await GetJsonAsync<
            List<DeadlockHeroCombStats>
        >(
            requestUri,
            cancellationToken
        );
    }

    private async Task<T> GetJsonAsync<T>(
        string requestUri,
        CancellationToken cancellationToken
    )
    {
        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                requestUri
            );

        using var response =
            await _httpClient.SendAsync(
                request,
                HttpCompletionOption
                    .ResponseHeadersRead,

                cancellationToken
            );

        var responseBody =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken
                );

        if (
            !response.IsSuccessStatusCode
        )
        {
            throw new DeadlockApiException(
                response.StatusCode,
                responseBody,
                ReadDiagnosticHeaders(
                    response
                )
            );
        }

        var result =
            JsonSerializer.Deserialize<T>(
                responseBody,
                JsonOptions
            );

        if (result is null)
        {
            throw new InvalidOperationException(
                "Deadlock API returned empty " +
                "or invalid JSON."
            );
        }

        return result;
    }

    private static IReadOnlyDictionary<
        string,
        string
    > ReadDiagnosticHeaders(
        HttpResponseMessage response
    )
    {
        var result =
            new Dictionary<
                string,
                string
            >(
                StringComparer
                    .OrdinalIgnoreCase
            );

        foreach (
            var header in
            response.Headers
        )
        {
            if (
                header.Key.Contains(
                    "RateLimit",
                    StringComparison
                        .OrdinalIgnoreCase
                ) ||
                header.Key.Equals(
                    "Retry-After",
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                result[header.Key] =
                    string.Join(
                        ",",
                        header.Value
                    );
            }
        }

        foreach (
            var header in
            response.Content.Headers
        )
        {
            if (
                header.Key.Contains(
                    "RateLimit",
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                result[header.Key] =
                    string.Join(
                        ",",
                        header.Value
                    );
            }
        }

        return result;
    }
}

internal sealed record DeadlockLaneMatchupStats(
    [property: JsonPropertyName(
        "assigned_lane"
    )]
    int AssignedLane,

    [property: JsonPropertyName(
        "hero_ids"
    )]
    IReadOnlyList<uint> HeroIds,

    [property: JsonPropertyName(
        "enemy_hero_ids"
    )]
    IReadOnlyList<uint> EnemyHeroIds,

    [property: JsonPropertyName(
        "wins"
    )]
    ulong Wins,

    [property: JsonPropertyName(
        "matches_played"
    )]
    ulong MatchesPlayed,

    /*
     * The endpoint is still unstable.
     *
     * According to the official schema, this should
     * always be a JSON number, but in
     * practice, one invalid token
     * must not invalidate the entire response.
     */
    [property: JsonPropertyName(
        "net_worth_diff"
    )]
    JsonElement NetWorthDiffRaw,

    [property: JsonPropertyName(
        "sample_matches"
    )]
    ulong SampleMatches
)
{
    /*
     * The rest of Lane Advisor continues
     * to use the existing internal names.
     *
     * Therefore no service/scoring changes
     * are required because of the API parsing guard.
     */
    [JsonIgnore]
    public double NetWorthDiff15Min
    {
        get
        {
            return TryGetNetWorthDiff(
                out var value
            )
                ? value
                : 0;
        }
    }

    /*
     * If this row's net_worth_diff
     * is invalid, this row's sample
     * is completely excluded from S15 aggregation.
     *
     * Wins/MatchesPlayed still
     * participate in WR.
     */
    [JsonIgnore]
    public ulong NetWorthMatches
    {
        get
        {
            return TryGetNetWorthDiff(
                out _
            )
                ? SampleMatches
                : 0;
        }
    }

    private bool TryGetNetWorthDiff(
        out double value
    )
    {
        value =
            0;

        if (
            NetWorthDiffRaw.ValueKind ==
                JsonValueKind.Number
        )
        {
            if (
                !NetWorthDiffRaw.TryGetDouble(
                    out value
                ) ||
                !double.IsFinite(
                    value
                )
            )
            {
                value =
                    0;

                return false;
            }

            return true;
        }

        /*
         * According to the schema, strings should not occur here.
         *
         * But if the API ever returns
         * a numeric value as a string, we can
         * safely accept it.
         *
         * NaN / Infinity are still rejected.
         */
        if (
            NetWorthDiffRaw.ValueKind ==
                JsonValueKind.String
        )
        {
            var text =
                NetWorthDiffRaw.GetString();

            if (
                !String.IsNullOrWhiteSpace(
                    text
                ) &&
                double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value
                ) &&
                double.IsFinite(
                    value
                )
            )
            {
                return true;
            }

            value =
                0;

            return false;
        }

        /*
         * null / object / array / bool /
         * undefined is treated as the absence of
         * a valid S15 reading.
         */
        return false;
    }
}

internal sealed record DeadlockHeroCombStats(
    [property: JsonPropertyName(
        "hero_ids"
    )]
    IReadOnlyList<uint> HeroIds,

    [property: JsonPropertyName(
        "wins"
    )]
    ulong Wins,

    [property: JsonPropertyName(
        "losses"
    )]
    ulong Losses,

    [property: JsonPropertyName(
        "matches"
    )]
    ulong Matches
);

internal sealed record DeadlockHeroStats(
    [property: JsonPropertyName(
        "account_id"
    )]
    uint AccountId,

    [property: JsonPropertyName(
        "hero_id"
    )]
    uint HeroId,

    [property: JsonPropertyName(
        "matches_played"
    )]
    ulong MatchesPlayed,

    [property: JsonPropertyName(
        "last_played"
    )]
    uint LastPlayed,

    [property: JsonPropertyName(
        "time_played"
    )]
    ulong TimePlayed,

    [property: JsonPropertyName(
        "wins"
    )]
    ulong Wins,

    [property: JsonPropertyName(
        "ending_level"
    )]
    double EndingLevel,

    [property: JsonPropertyName(
        "kills"
    )]
    ulong Kills,

    [property: JsonPropertyName(
        "deaths"
    )]
    ulong Deaths,

    [property: JsonPropertyName(
        "assists"
    )]
    ulong Assists,

    [property: JsonPropertyName(
        "total_player_damage"
    )]
    ulong TotalPlayerDamage,

    [property: JsonPropertyName(
        "total_player_damage_taken"
    )]
    ulong TotalPlayerDamageTaken,

    [property: JsonPropertyName(
        "total_boss_damage"
    )]
    ulong TotalBossDamage,

    [property: JsonPropertyName(
        "total_creep_damage"
    )]
    ulong TotalCreepDamage,

    [property: JsonPropertyName(
        "total_neutral_damage"
    )]
    ulong TotalNeutralDamage,

    [property: JsonPropertyName(
        "denies_per_match"
    )]
    double DeniesPerMatch,

    [property: JsonPropertyName(
        "kills_per_min"
    )]
    double KillsPerMinute,

    [property: JsonPropertyName(
        "deaths_per_min"
    )]
    double DeathsPerMinute,

    [property: JsonPropertyName(
        "assists_per_min"
    )]
    double AssistsPerMinute,

    [property: JsonPropertyName(
        "denies_per_min"
    )]
    double DeniesPerMinute,

    [property: JsonPropertyName(
        "networth_per_min"
    )]
    double NetworthPerMinute,

    [property: JsonPropertyName(
        "last_hits_per_min"
    )]
    double LastHitsPerMinute,

    [property: JsonPropertyName(
        "damage_per_min"
    )]
    double DamagePerMinute,

    [property: JsonPropertyName(
        "damage_per_soul"
    )]
    double DamagePerSoul,

    [property: JsonPropertyName(
        "damage_mitigated_per_min"
    )]
    double DamageMitigatedPerMinute,

    [property: JsonPropertyName(
        "damage_taken_per_min"
    )]
    double DamageTakenPerMinute,

    [property: JsonPropertyName(
        "damage_taken_per_soul"
    )]
    double DamageTakenPerSoul,

    [property: JsonPropertyName(
        "creeps_per_min"
    )]
    double CreepsPerMinute,

    [property: JsonPropertyName(
        "obj_damage_per_min"
    )]
    double ObjectiveDamagePerMinute,

    [property: JsonPropertyName(
        "obj_damage_per_soul"
    )]
    double ObjectiveDamagePerSoul,

    [property: JsonPropertyName(
        "accuracy"
    )]
    double Accuracy,

    [property: JsonPropertyName(
        "crit_shot_rate"
    )]
    double CriticalShotRate,

    [property: JsonPropertyName(
        "matches"
    )]
    IReadOnlyList<ulong>? Matches
)
{
    [JsonIgnore]
    public double WinRatePercent =>
        MatchesPlayed == 0
            ? 0
            : 100.0 *
                Wins /
                MatchesPlayed;
}

internal sealed class DeadlockApiException :
    Exception
{
    public DeadlockApiException(
        HttpStatusCode statusCode,
        string responseBody,
        IReadOnlyDictionary<
            string,
            string
        > diagnosticHeaders
    )
        : base(
            CreateMessage(
                statusCode,
                responseBody
            )
        )
    {
        StatusCode =
            statusCode;

        ResponseBody =
            responseBody;

        DiagnosticHeaders =
            diagnosticHeaders;
    }

    public HttpStatusCode StatusCode
    {
        get;
    }

    public string ResponseBody
    {
        get;
    }

    public IReadOnlyDictionary<
        string,
        string
    > DiagnosticHeaders
    {
        get;
    }

    private static string CreateMessage(
        HttpStatusCode statusCode,
        string responseBody
    )
    {
        var body =
            string.IsNullOrWhiteSpace(
                responseBody
            )
                ? "<empty>"
                : responseBody;

        if (body.Length > 2000)
        {
            body =
                body[..2000] +
                "...";
        }

        return
            "Deadlock API returned HTTP " +
            (int)statusCode +
            " " +
            statusCode +
            ": " +
            body;
    }
}