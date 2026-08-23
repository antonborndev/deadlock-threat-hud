using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DeadlockHeroCatalogService :
    IDisposable
{
    private static readonly JsonSerializerOptions
        JsonOptions =
            new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive =
                    true
            };

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _cacheLifetime;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _gate =
        new(
            1,
            1
        );

    private DateTimeOffset _loadedAtUtc =
        DateTimeOffset.MinValue;

    private IReadOnlyList<DeadlockHeroAsset>
        _heroes =
            Array.Empty<DeadlockHeroAsset>();

    private IReadOnlyDictionary<
        string,
        IReadOnlyList<DeadlockHeroAsset>
    > _heroesByAlias =
        new Dictionary<
            string,
            IReadOnlyList<DeadlockHeroAsset>
        >(
            StringComparer.Ordinal
        );

    public DeadlockHeroCatalogService(
        HttpClient httpClient,
        TimeSpan cacheLifetime,
        Action<string>? log = null
    )
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(
                nameof(httpClient)
            );

        _cacheLifetime =
            cacheLifetime;

        _log =
            log ??
            (_ => { });
    }

    public async Task<
        IReadOnlyList<HeroNameResolution>
    > ResolveAsync(
        IEnumerable<string> heroNames,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            heroNames
        );

        await EnsureLoadedAsync(
            cancellationToken
        );

        var result =
            new List<HeroNameResolution>();

        foreach (
            var inputValue in heroNames
        )
        {
            var inputName =
                inputValue ??
                string.Empty;

            var normalizedName =
                NormalizeName(
                    inputName
                );

            if (
                string.IsNullOrEmpty(
                    normalizedName
                )
            )
            {
                result.Add(
                    HeroNameResolution.Unknown(
                        inputName,
                        normalizedName
                    )
                );

                continue;
            }

            if (
                !_heroesByAlias.TryGetValue(
                    normalizedName,
                    out var candidates
                ) ||
                candidates.Count == 0
            )
            {
                result.Add(
                    HeroNameResolution.Unknown(
                        inputName,
                        normalizedName
                    )
                );

                continue;
            }

            if (candidates.Count == 1)
            {
                var hero =
                    candidates[0];

                result.Add(
                    HeroNameResolution.Resolved(
                        inputName,
                        normalizedName,
                        hero
                    )
                );

                continue;
            }

            result.Add(
                HeroNameResolution.Ambiguous(
                    inputName,
                    normalizedName,
                    candidates
                )
            );
        }

        return result;
    }

    public async Task<
        IReadOnlyList<DeadlockHeroAsset>
    > GetHeroesAsync(
        CancellationToken cancellationToken
    )
    {
        await EnsureLoadedAsync(
            cancellationToken
        );

        return _heroes;
    }

    private async Task EnsureLoadedAsync(
        CancellationToken cancellationToken
    )
    {
        var now =
            DateTimeOffset.UtcNow;

        if (
            _heroes.Count > 0 &&
            now -
                _loadedAtUtc <
                _cacheLifetime
        )
        {
            return;
        }

        await _gate.WaitAsync(
            cancellationToken
        );

        try
        {
            now =
                DateTimeOffset.UtcNow;

            if (
                _heroes.Count > 0 &&
                now -
                    _loadedAtUtc <
                    _cacheLifetime
            )
            {
                return;
            }

            _log(
                "Deadlock hero catalog: REQUEST"
            );

            using var response =
                await _httpClient.GetAsync(
                    "v1/assets/heroes" +
                    "?only_active=true" +
                    "&language=english",

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
                throw new InvalidOperationException(
                    "Deadlock hero catalog returned HTTP " +
                    (int)response.StatusCode +
                    " " +
                    response.StatusCode +
                    ": " +
                    LimitText(
                        responseBody,
                        2000
                    )
                );
            }

            var heroes =
                JsonSerializer.Deserialize<
                    List<DeadlockHeroAsset>
                >(
                    responseBody,
                    JsonOptions
                );

            if (heroes is null)
            {
                throw new InvalidOperationException(
                    "Deadlock hero catalog returned " +
                    "invalid JSON."
                );
            }

            var validHeroes =
                heroes
                    .Where(
                        hero =>
                            hero.Id > 0 &&
                            !string.IsNullOrWhiteSpace(
                                hero.Name
                            )
                    )
                    .OrderBy(
                        hero =>
                            hero.Id
                    )
                    .ToArray();

            if (validHeroes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Deadlock hero catalog contains " +
                    "no valid heroes."
                );
            }

            _heroes =
                validHeroes;

            _heroesByAlias =
                BuildAliasIndex(
                    validHeroes
                );

            _loadedAtUtc =
                now;

            _log(
                "Deadlock hero catalog: READY" +
                " | heroes=" +
                _heroes.Count +
                " | aliases=" +
                _heroesByAlias.Count
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<DeadlockHeroAsset>
    > BuildAliasIndex(
        IReadOnlyList<DeadlockHeroAsset> heroes
    )
    {
        var mutableIndex =
            new Dictionary<
                string,
                Dictionary<
                    uint,
                    DeadlockHeroAsset
                >
            >(
                StringComparer.Ordinal
            );

        foreach (
            var hero in heroes
        )
        {
            AddAlias(
                mutableIndex,
                hero.Name,
                hero
            );

            AddAlias(
                mutableIndex,
                hero.ClassName,
                hero
            );

            if (
                !string.IsNullOrWhiteSpace(
                    hero.ClassName
                ) &&
                hero.ClassName.StartsWith(
                    "hero_",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                AddAlias(
                    mutableIndex,
                    hero.ClassName[
                        "hero_".Length..
                    ],
                    hero
                );
            }
        }

        return mutableIndex.ToDictionary(
            pair =>
                pair.Key,

            pair =>
                (
                    IReadOnlyList<
                        DeadlockHeroAsset
                    >
                )
                pair.Value
                    .Values
                    .OrderBy(
                        hero =>
                            hero.Id
                    )
                    .ToArray(),

            StringComparer.Ordinal
        );
    }

    private static void AddAlias(
        IDictionary<
            string,
            Dictionary<
                uint,
                DeadlockHeroAsset
            >
        > index,
        string? alias,
        DeadlockHeroAsset hero
    )
    {
        var normalizedAlias =
            NormalizeName(
                alias
            );

        if (
            string.IsNullOrEmpty(
                normalizedAlias
            )
        )
        {
            return;
        }

        if (
            !index.TryGetValue(
                normalizedAlias,
                out var heroes
            )
        )
        {
            heroes =
                new Dictionary<
                    uint,
                    DeadlockHeroAsset
                >();

            index[normalizedAlias] =
                heroes;
        }

        heroes[hero.Id] =
            hero;
    }

    internal static string NormalizeName(
        string? value
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                value
            )
        )
        {
            return string.Empty;
        }

        var unicodeNormalized =
            value.Normalize(
                NormalizationForm.FormKC
            );

        var builder =
            new StringBuilder(
                unicodeNormalized.Length
            );

        var pendingSpace =
            false;

        foreach (
            var character in
            unicodeNormalized
        )
        {
            if (
                char.IsWhiteSpace(
                    character
                )
            )
            {
                if (builder.Length > 0)
                {
                    pendingSpace =
                        true;
                }

                continue;
            }

            if (
                pendingSpace &&
                builder.Length > 0
            )
            {
                builder.Append(
                    ' '
                );

                pendingSpace =
                    false;
            }

            builder.Append(
                char.ToLowerInvariant(
                    character
                )
            );
        }

        return builder
            .ToString()
            .Trim();
    }

    private static string LimitText(
        string value,
        int maximumLength
    )
    {
        if (
            value.Length <=
            maximumLength
        )
        {
            return value;
        }

        return value[
            ..maximumLength
        ] +
        "...";
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

internal sealed record DeadlockHeroAsset(
    [property: JsonPropertyName(
        "id"
    )]
    uint Id,

    [property: JsonPropertyName(
        "class_name"
    )]
    string? ClassName,

    [property: JsonPropertyName(
        "name"
    )]
    string Name,

    [property: JsonPropertyName(
        "images"
    )]
    DeadlockHeroImages? Images = null
);

internal sealed record DeadlockHeroImages(
    [property: JsonPropertyName(
        "icon_image_small"
    )]
    string? IconImageSmall
);

internal sealed record DeadlockHeroCandidate(
    uint HeroId,
    string ApiName,
    string? ClassName
);

internal sealed record HeroNameResolution(
    string InputName,
    string NormalizedName,
    string Status,
    uint? HeroId,
    string? ApiName,
    string? ClassName,
    IReadOnlyList<DeadlockHeroCandidate>
        Candidates
)
{
    public static HeroNameResolution Resolved(
        string inputName,
        string normalizedName,
        DeadlockHeroAsset hero
    )
    {
        var candidate =
            ToCandidate(
                hero
            );

        return new HeroNameResolution(
            inputName,
            normalizedName,
            "resolved",
            hero.Id,
            hero.Name,
            hero.ClassName,
            new[]
            {
                candidate
            }
        );
    }

    public static HeroNameResolution Unknown(
        string inputName,
        string normalizedName
    )
    {
        return new HeroNameResolution(
            inputName,
            normalizedName,
            "unknown",
            null,
            null,
            null,
            Array.Empty<
                DeadlockHeroCandidate
            >()
        );
    }

    public static HeroNameResolution Ambiguous(
        string inputName,
        string normalizedName,
        IReadOnlyList<DeadlockHeroAsset>
            heroes
    )
    {
        return new HeroNameResolution(
            inputName,
            normalizedName,
            "ambiguous",
            null,
            null,
            null,
            heroes
                .Select(
                    ToCandidate
                )
                .ToArray()
        );
    }

    private static DeadlockHeroCandidate
        ToCandidate(
            DeadlockHeroAsset hero
        )
    {
        return new DeadlockHeroCandidate(
            hero.Id,
            hero.Name,
            hero.ClassName
        );
    }
}