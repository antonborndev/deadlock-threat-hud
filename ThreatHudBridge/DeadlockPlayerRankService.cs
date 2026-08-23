using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DeadlockPlayerRankService :
    IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _cacheLifetime;

    /*
     * Limit concurrency.
     *
     * For 12 players, there will be
     * at most four concurrent requests.
     */
    private readonly SemaphoreSlim _requestGate;

    private readonly ConcurrentDictionary<
        uint,
        CachedPlayerRank
    > _cache =
        new();

    public DeadlockPlayerRankService(
        HttpClient httpClient,
        TimeSpan cacheLifetime,
        int maximumConcurrency = 4
    )
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(
                nameof(httpClient)
            );

        _cacheLifetime =
            cacheLifetime;

        if (maximumConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency)
            );
        }

        _requestGate =
            new SemaphoreSlim(
                maximumConcurrency,
                maximumConcurrency
            );
    }

    public async Task<
        IReadOnlyList<
            DeadlockPlayerRankResult
        >
    > GetRanksAsync(
        IReadOnlyList<uint> accountIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            accountIds
        );

        var tasks =
            new Task<
                DeadlockPlayerRankResult
            >[
                accountIds.Count
            ];

        /*
         * Task order preserves the order
         * of the original accountIDs.
         */
        for (
            var index = 0;
            index < accountIds.Count;
            index++
        )
        {
            tasks[index] =
                GetRankAsync(
                    accountIds[index],
                    cancellationToken
                );
        }

        return await Task.WhenAll(
            tasks
        );
    }

    private async Task<
        DeadlockPlayerRankResult
    > GetRankAsync(
        uint accountId,
        CancellationToken cancellationToken
    )
    {
        if (accountId == 0)
        {
            return new DeadlockPlayerRankResult(
                AccountId:
                    accountId,

                Status:
                    DeadlockPlayerRankStatus
                        .ApiError,

                Rank:
                    0,

                Subrank:
                    0
            );
        }

        if (
            TryGetCached(
                accountId,
                out var cachedResult
            )
        )
        {
            return cachedResult;
        }

        await _requestGate.WaitAsync(
            cancellationToken
        );

        try
        {
            /*
             * While the request was waiting for the semaphore,
             * another request may already have populated the cache.
             */
            if (
                TryGetCached(
                    accountId,
                    out cachedResult
                )
            )
            {
                return cachedResult;
            }

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,

                    "v1/players/" +
                    accountId +
                    "/rank"
                );

            request.Headers.Accept.Clear();

            request.Headers.Accept.ParseAdd(
                "application/json"
            );

            using var response =
                await _httpClient.SendAsync(
                    request,

                    HttpCompletionOption
                        .ResponseHeadersRead,

                    cancellationToken
                );

            if (
                response.StatusCode ==
                    HttpStatusCode.Forbidden
            )
            {
                return StoreResult(
                    new DeadlockPlayerRankResult(
                        AccountId:
                            accountId,

                        Status:
                            DeadlockPlayerRankStatus
                                .Protected,

                        Rank:
                            0,

                        Subrank:
                            0
                    )
                );
            }

            if (
                response.StatusCode ==
                    HttpStatusCode.NotFound
            )
            {
                return StoreResult(
                    new DeadlockPlayerRankResult(
                        AccountId:
                            accountId,

                        Status:
                            DeadlockPlayerRankStatus
                                .NotFound,

                        Rank:
                            0,

                        Subrank:
                            0
                    )
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DeadlockPlayerRankResult(
                    AccountId:
                        accountId,

                    Status:
                        DeadlockPlayerRankStatus
                            .ApiError,

                    Rank:
                        0,

                    Subrank:
                        0
                );
            }

            await using var responseStream =
                await response.Content
                    .ReadAsStreamAsync(
                        cancellationToken
                    );

            var apiResponse =
                await JsonSerializer
                    .DeserializeAsync<
                        DeadlockRankApiResponse
                    >(
                        responseStream,

                        cancellationToken:
                            cancellationToken
                    );

            if (apiResponse is null)
            {
                return new DeadlockPlayerRankResult(
                    AccountId:
                        accountId,

                    Status:
                        DeadlockPlayerRankStatus
                            .ApiError,

                    Rank:
                        0,

                    Subrank:
                        0
                );
            }

            /*
             * Zero means:
             *
             * - rank is unavailable;
             * - the player is still calibrating;
             * - the API found no ranked match with a rank.
             */
            if (
                apiResponse.Badge == 0 ||
                apiResponse.Rank == 0 ||
                apiResponse.Subrank == 0
            )
            {
                return StoreResult(
                    new DeadlockPlayerRankResult(
                        AccountId:
                            accountId,

                        Status:
                            DeadlockPlayerRankStatus
                                .Unranked,

                        Rank:
                            0,

                        Subrank:
                            0
                    )
                );
            }

            if (
                apiResponse.Rank > 11 ||
                apiResponse.Subrank > 6
            )
            {
                return new DeadlockPlayerRankResult(
                    AccountId:
                        accountId,

                    Status:
                        DeadlockPlayerRankStatus
                            .ApiError,

                    Rank:
                        0,

                    Subrank:
                        0
                );
            }

            return StoreResult(
                new DeadlockPlayerRankResult(
                    AccountId:
                        accountId,

                    Status:
                        DeadlockPlayerRankStatus.Ok,

                    Rank:
                        checked(
                            (byte)apiResponse.Rank
                        ),

                    Subrank:
                        checked(
                            (byte)apiResponse.Subrank
                        )
                )
            );
        }
        catch (
            HttpRequestException
        )
        {
            /*
             * Network errors are not cached:
             * the next attempt can retry the request.
             */
            return new DeadlockPlayerRankResult(
                AccountId:
                    accountId,

                Status:
                    DeadlockPlayerRankStatus
                        .ApiError,

                Rank:
                    0,

                Subrank:
                    0
            );
        }
        catch (
            JsonException
        )
        {
            return new DeadlockPlayerRankResult(
                AccountId:
                    accountId,

                Status:
                    DeadlockPlayerRankStatus
                        .ApiError,

                Rank:
                    0,

                Subrank:
                    0
            );
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private bool TryGetCached(
        uint accountId,
        out DeadlockPlayerRankResult result
    )
    {
        if (
            _cache.TryGetValue(
                accountId,
                out var cached
            ) &&
            DateTimeOffset.UtcNow -
                cached.CreatedAtUtc <
                _cacheLifetime
        )
        {
            result =
                cached.Result;

            return true;
        }

        result =
            default!;

        return false;
    }

    private DeadlockPlayerRankResult StoreResult(
        DeadlockPlayerRankResult result
    )
    {
        _cache[result.AccountId] =
            new CachedPlayerRank(
                CreatedAtUtc:
                    DateTimeOffset.UtcNow,

                Result:
                    result
            );

        return result;
    }

    public void Dispose()
    {
        _requestGate.Dispose();
    }
}

internal sealed record DeadlockRankApiResponse(
    [property: JsonPropertyName("badge")]
    uint Badge,

    [property: JsonPropertyName("rank")]
    uint Rank,

    [property: JsonPropertyName("subrank")]
    uint Subrank
);

internal sealed record DeadlockPlayerRankResult(
    uint AccountId,
    DeadlockPlayerRankStatus Status,
    byte Rank,
    byte Subrank
);

internal sealed record CachedPlayerRank(
    DateTimeOffset CreatedAtUtc,
    DeadlockPlayerRankResult Result
);

internal enum DeadlockPlayerRankStatus : byte
{
    Ok =
        0,

    Unranked =
        1,

    Protected =
        2,

    NotFound =
        3,

    ApiError =
        4
}