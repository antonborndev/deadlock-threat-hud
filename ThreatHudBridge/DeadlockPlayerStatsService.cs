internal sealed class DeadlockPlayerStatsService :
    IDisposable
{
    private readonly DeadlockApiClient _client;
    private readonly TimeSpan _cacheLifetime;
    private readonly Action<string> _log;

    private readonly SemaphoreSlim _gate =
        new(
            1,
            1
        );

    private string? _cachedRequestKey;

    private DateTimeOffset _cachedAtUtc;

    private IReadOnlyList<DeadlockHeroStats>
        _cachedStats =
            Array.Empty<
                DeadlockHeroStats
            >();

    public DeadlockPlayerStatsService(
        DeadlockApiClient client,
        TimeSpan cacheLifetime,
        Action<string>? log = null
    )
    {
        _client =
            client ??
            throw new ArgumentNullException(
                nameof(client)
            );

        _cacheLifetime =
            cacheLifetime;

        _log =
            log ??
            (_ => { });
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

        var heroIdArray =
            heroIds?
                .Distinct()
                .OrderBy(
                    value =>
                        value
                )
                .ToArray() ??
            Array.Empty<uint>();

        if (
            accountIdArray.Length == 0
        )
        {
            return
                Array.Empty<
                    DeadlockHeroStats
                >();
        }

        var requestKey =
            string.Join(
                ",",
                accountIdArray
            ) +
            "|" +
            string.Join(
                ",",
                heroIdArray
            );

        await _gate.WaitAsync(
            cancellationToken
        );

        try
        {
            var now =
                DateTimeOffset.UtcNow;

            if (
                requestKey ==
                    _cachedRequestKey &&
                now -
                    _cachedAtUtc <
                    _cacheLifetime
            )
            {
                _log(
                    "Deadlock API cache HIT: " +
                    $"accounts={accountIdArray.Length}, " +
                    $"heroes={heroIdArray.Length}, " +
                    $"rows={_cachedStats.Count}"
                );

                return _cachedStats;
            }

            _log(
                "Deadlock API request: " +
                $"accounts={accountIdArray.Length}, " +
                $"heroes={heroIdArray.Length}"
            );

            var stats =
                await _client
                    .GetHeroStatsAsync(
                        accountIdArray,
                        heroIdArray,
                        cancellationToken
                    );

            _cachedRequestKey =
                requestKey;

            _cachedAtUtc =
                now;

            _cachedStats =
                stats.ToArray();

            _log(
                "Deadlock API response: " +
                $"rows={_cachedStats.Count}"
            );

            return _cachedStats;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}