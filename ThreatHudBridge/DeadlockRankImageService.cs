using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

internal sealed class DeadlockRankImageService :
    IDisposable
{
    /*
     * Must match the box
     * in CurrentMatchRankOverlay.
     */
    private const int RankImageDisplayBoxPixels =
        38;

    private const int WhiteOutlinePixels =
        2;

    private const int MaximumSourcePngBytes =
        4 * 1024 * 1024;

    private readonly HttpClient
        _httpClient;

    private readonly TimeSpan
        _cacheLifetime;

    private readonly SemaphoreSlim
        _pipelineGate =
            new(
                4,
                4
            );

    /*
     * GDI decoding and outline generation use full-frame buffers. Downloads
     * may overlap, but only one image is allowed through that memory-heavy
     * stage at a time.
     */
    private readonly SemaphoreSlim
        _processingGate =
            new(
                1,
                1
            );

    /*
     * A global semaphore with count 4 is not a single-flight guard: four
     * simultaneous requests for the same rank can all observe a cache miss.
     * Rank/subrank has only 66 valid keys, so one small gate per key is bounded.
     */
    private readonly ConcurrentDictionary<
        int,
        SemaphoreSlim
    > _keyGates =
        new();

    /*
     * Key:
     *
     * rank * 10 + subrank
     *
     * The cache stores the already processed
     * PNG with a white outline.
     */
    private readonly ConcurrentDictionary<
        int,
        CachedRankImage
    > _cache =
        new();

    public DeadlockRankImageService(
        HttpClient httpClient,
        TimeSpan cacheLifetime
    )
    {
        _httpClient =
            httpClient ??
            throw new ArgumentNullException(
                nameof(httpClient)
            );

        _cacheLifetime =
            cacheLifetime;
    }

    public async Task<byte[]> GetPngAsync(
        byte rank,
        byte subrank,
        CancellationToken cancellationToken
    )
    {
        ValidateRank(
            rank,
            subrank
        );

        var cacheKey =
            rank *
            10 +
            subrank;

        if (
            TryGetCached(
                cacheKey,
                out var cachedBytes
            )
        )
        {
            return cachedBytes;
        }

        var keyGate =
            _keyGates.GetOrAdd(
                cacheKey,
                static _ =>
                    new SemaphoreSlim(
                        1,
                        1
                    )
            );

        await keyGate.WaitAsync(
            cancellationToken
        );

        try
        {
            /*
             * The second check is protected by the per-key gate, so exactly
             * one request downloads and processes a missing rank image.
             */
            if (
                TryGetCached(
                    cacheKey,
                    out cachedBytes
                )
            )
            {
                return cachedBytes;
            }

            byte[] outlinedBytes;

            /*
             * Keep one of four bounded pipeline slots until processing is
             * complete. Otherwise many different keys could finish their
             * downloads and retain up to 4 MiB each while waiting for the
             * single GDI slot.
             */
            await _pipelineGate.WaitAsync(
                cancellationToken
            );

            try
            {
                var sourceBytes =
                    await DownloadPngAsync(
                        rank,
                        subrank,
                        cancellationToken
                    );

                await _processingGate.WaitAsync(
                    cancellationToken
                );

                try
                {
                    outlinedBytes =
                        RankImageAlphaOutlineProcessor
                            .AddWhiteOutline(
                                sourceBytes,
                                RankImageDisplayBoxPixels,
                                WhiteOutlinePixels,
                                cancellationToken
                            );
                }
                catch (
                    OperationCanceledException
                )
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new DeadlockRankImageException(
                        HttpStatusCode.BadGateway,

                        "Failed to add the white alpha outline" +
                        $" | rank={rank}" +
                        $" | subrank={subrank}" +
                        $" | sourceBytes={sourceBytes.Length}" +
                        $" | error={error.Message}",

                        error
                    );
                }
                finally
                {
                    _processingGate.Release();
                }
            }
            finally
            {
                _pipelineGate.Release();
            }

            if (!IsPng(outlinedBytes))
            {
                throw new DeadlockRankImageException(
                    HttpStatusCode.BadGateway,

                    "Outline processor returned an invalid PNG" +
                    $" | rank={rank}" +
                    $" | subrank={subrank}" +
                    $" | bytes={outlinedBytes.Length}"
                );
            }

            _cache[cacheKey] =
                new CachedRankImage(
                    CreatedAtUtc:
                        DateTimeOffset.UtcNow,

                    Bytes:
                        outlinedBytes
                );

            return outlinedBytes;
        }
        finally
        {
            keyGate.Release();
        }
    }

    private async Task<byte[]> DownloadPngAsync(
        byte rank,
        byte subrank,
        CancellationToken cancellationToken
    )
    {
        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,

                $"v1/assets/ranks/{rank}/" +
                $"{subrank}/image?format=png"
            );

        request.Headers.Accept.Clear();

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "image/png"
            )
        );

        using var response =
            await _httpClient.SendAsync(
                request,

                HttpCompletionOption
                    .ResponseHeadersRead,

                cancellationToken
            );

        if (!response.IsSuccessStatusCode)
        {
            var responseText =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken
                    );

            throw new DeadlockRankImageException(
                response.StatusCode,

                "Deadlock API did not return a rank image" +
                $" | rank={rank}" +
                $" | subrank={subrank}" +
                $" | status={(int)response.StatusCode}" +
                $" | response={responseText}"
            );
        }

        var contentType =
            response.Content
                .Headers
                .ContentType?
                .MediaType ??
            string.Empty;

        if (
            !string.Equals(
                contentType,
                "image/png",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new DeadlockRankImageException(
                HttpStatusCode.BadGateway,

                "Deadlock API returned an unexpected Content-Type" +
                $" | rank={rank}" +
                $" | subrank={subrank}" +
                $" | contentType={contentType}"
            );
        }

        var declaredLength =
            response.Content.Headers.ContentLength;

        if (
            declaredLength.HasValue &&
            declaredLength.Value >
                MaximumSourcePngBytes
        )
        {
            throw new DeadlockRankImageException(
                HttpStatusCode.BadGateway,

                "Deadlock API rank image is too large" +
                $" | rank={rank}" +
                $" | subrank={subrank}" +
                $" | bytes={declaredLength.Value}" +
                $" | maximum={MaximumSourcePngBytes}"
            );
        }

        var bytes =
            await ReadLimitedBytesAsync(
                response.Content,
                MaximumSourcePngBytes,
                cancellationToken
            );

        if (!IsPng(bytes))
        {
            throw new DeadlockRankImageException(
                HttpStatusCode.BadGateway,

                "Deadlock API returned an invalid PNG" +
                $" | rank={rank}" +
                $" | subrank={subrank}" +
                $" | bytes={bytes.Length}"
            );
        }

        return bytes;
    }

    private static async Task<byte[]>
        ReadLimitedBytesAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken
        )
    {
        await using var input =
            await content.ReadAsStreamAsync(
                cancellationToken
            );

        using var output =
            new MemoryStream();

        var buffer =
            new byte[64 * 1024];

        while (true)
        {
            var bytesRead =
                await input.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken
                );

            if (bytesRead == 0)
            {
                break;
            }

            if (
                output.Length +
                    bytesRead >
                maximumBytes
            )
            {
                throw new DeadlockRankImageException(
                    HttpStatusCode.BadGateway,
                    "Deadlock API rank image exceeded the size limit" +
                    $" | maximum={maximumBytes}"
                );
            }

            await output.WriteAsync(
                buffer.AsMemory(
                    0,
                    bytesRead
                ),
                cancellationToken
            );
        }

        return output.ToArray();
    }

    private bool TryGetCached(
        int cacheKey,
        out byte[] bytes
    )
    {
        if (
            _cache.TryGetValue(
                cacheKey,
                out var cached
            ) &&
            DateTimeOffset.UtcNow -
                cached.CreatedAtUtc <
                _cacheLifetime
        )
        {
            bytes =
                cached.Bytes;

            return true;
        }

        bytes =
            Array.Empty<byte>();

        return false;
    }

    private static void ValidateRank(
        byte rank,
        byte subrank
    )
    {
        if (
            rank < 1 ||
            rank > 11
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(rank),
                "rank must be from 1 to 11."
            );
        }

        if (
            subrank < 1 ||
            subrank > 6
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(subrank),
                "subrank must be from 1 to 6."
            );
        }
    }

    private static bool IsPng(
        byte[] bytes
    )
    {
        return
            bytes.Length >= 8 &&
            bytes[0] == 137 &&
            bytes[1] == 80 &&
            bytes[2] == 78 &&
            bytes[3] == 71 &&
            bytes[4] == 13 &&
            bytes[5] == 10 &&
            bytes[6] == 26 &&
            bytes[7] == 10;
    }

    public void Dispose()
    {
        _pipelineGate.Dispose();
        _processingGate.Dispose();

        foreach (var gate in _keyGates.Values)
        {
            gate.Dispose();
        }

        _keyGates.Clear();
    }
}

internal sealed record CachedRankImage(
    DateTimeOffset CreatedAtUtc,
    byte[] Bytes
);

internal sealed class DeadlockRankImageException :
    Exception
{
    public DeadlockRankImageException(
        HttpStatusCode statusCode,
        string message,
        Exception? innerException = null
    )
        : base(
            message,
            innerException
        )
    {
        StatusCode =
            statusCode;
    }

    public HttpStatusCode StatusCode
    {
        get;
    }
}
