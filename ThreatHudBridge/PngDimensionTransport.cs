using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.AspNetCore.Http;

internal sealed class PngDimensionTransport
{
    private const int DimensionBase =
        16;

    private const int BytesPerChunk =
        2;

    private const int MaximumSessionLength =
        80;

    private const int MaximumSessions =
        512;

    private const int MaximumCachedPngs =
        4_096;

    private const int MaximumPacketBytes =
        BridgeProtocol.HeaderSize +
        ushort.MaxValue;

    private readonly object _sessionGate =
        new();

    private readonly object _pngCacheGate =
        new();

    private readonly TimeSpan _sessionLifetime;
    private readonly Action<string> _log;

    private readonly Dictionary<
        string,
        TransportSession
    > _sessions =
        new();

    private readonly Dictionary<
        int,
        LinkedListNode<PngCacheEntry>
    > _pngCache =
        new();

    private readonly LinkedList<PngCacheEntry>
        _pngCacheLru =
            new();

    public PngDimensionTransport(
        TimeSpan sessionLifetime,
        Action<string>? log = null
    )
    {
        _sessionLifetime =
            sessionLifetime;

        _log =
            log ??
            (_ => { });
    }

    public IResult CreateChunkResult(
        string channel,
        string session,
        int chunkIndex,
        Func<byte[]> packetFactory
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                channel
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    ok = false,

                    error =
                        "The channel parameter is required."
                }
            );
        }

        if (
            !IsValidSession(
                session
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    ok = false,

                    error =
                        "Invalid session parameter."
                }
            );
        }

        if (chunkIndex < 0)
        {
            return Results.BadRequest(
                new
                {
                    ok = false,

                    error =
                        "chunk cannot be negative."
                }
            );
        }

        TransportSession transportSession;

        try
        {
            transportSession =
                GetOrCreateSession(
                    channel,
                    session,
                    packetFactory
                );

            if (
                transportSession.Failure is
                    not null
            )
            {
                transportSession
                    .Failure
                    .Throw();

                throw new InvalidOperationException(
                    "Cached transport failure did not throw."
                );
            }
        }
        catch (Exception error)
        {
            _log(
                "Transport session ERROR: " +
                error
            );

            return Results.Problem(
                title:
                    "Failed to build " +
                    "transport packet.",

                detail:
                    error.Message,

                statusCode:
                    StatusCodes
                        .Status500InternalServerError
            );
        }

        var packet =
            transportSession.Packet ??
            throw new InvalidOperationException(
                "Transport session has no packet."
            );

        var chunkCount =
            GetChunkCount(
                packet.Length
            );

        if (
            chunkIndex >=
            chunkCount
        )
        {
            return Results.BadRequest(
                new
                {
                    ok = false,

                    error =
                        "Requested chunk is outside " +
                        "packet.",

                    chunkIndex,
                    chunkCount
                }
            );
        }

        var byteOffset =
            chunkIndex *
            BytesPerChunk;

        var byte0 =
            packet[
                byteOffset
            ];

        var byte1 =
            byteOffset + 1 <
            packet.Length

                ? packet[
                    byteOffset + 1
                ]

                : (byte)0;

        _log(
            "Transport chunk: " +
            $"channel={channel}, " +
            $"session={session}, " +
            $"chunk={chunkIndex}/{chunkCount - 1}, " +
            $"bytes={byte0},{byte1}"
        );

        return Results.File(
            GetOrCreatePng(
                byte0,
                byte1
            ),
            "image/png"
        );
    }

    private TransportSession GetOrCreateSession(
        string channel,
        string session,
        Func<byte[]> packetFactory
    )
    {
        var key =
            channel +
            ":" +
            session;

        lock (_sessionGate)
        {
            RemoveExpiredSessions();

            if (
                _sessions.TryGetValue(
                    key,
                    out var existing
                )
            )
            {
                return existing;
            }

            try
            {
                var packet =
                    packetFactory();

                if (packet.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Payload factory returned " +
                        "an empty packet."
                    );
                }

                if (
                    packet.Length >
                        MaximumPacketBytes
                )
                {
                    throw new InvalidOperationException(
                        "Payload factory returned an oversized packet" +
                        $" | bytes={packet.Length}" +
                        $" | maximum={MaximumPacketBytes}"
                    );
                }

                var created =
                    new TransportSession(
                        DateTimeOffset.UtcNow,
                        packet,
                        Failure:
                            null
                    );

                _sessions[key] =
                    created;

                TrimSessions();

                _log(
                    "Transport session CREATED: " +
                    $"channel={channel}, " +
                    $"session={session}, " +
                    $"packetBytes={packet.Length}, " +
                    $"chunks={GetChunkCount(packet.Length)}"
                );

                return created;
            }
            catch (Exception error)
            {
                var failed =
                    new TransportSession(
                        DateTimeOffset.UtcNow,
                        Packet:
                            null,
                        Failure:
                            ExceptionDispatchInfo
                                .Capture(
                                    error
                                )
                    );

                _sessions[key] =
                    failed;

                TrimSessions();

                _log(
                    "Transport session FAILED: " +
                    $"channel={channel}, " +
                    $"session={session}, " +
                    $"error={error.Message}"
                );

                return failed;
            }
        }
    }

    private void RemoveExpiredSessions()
    {
        var now =
            DateTimeOffset.UtcNow;

        var expiredKeys =
            _sessions
                .Where(
                    pair =>
                        now -
                        pair.Value.CreatedAtUtc >
                        _sessionLifetime
                )
                .Select(
                    pair =>
                        pair.Key
                )
                .ToList();

        foreach (
            var key in expiredKeys
        )
        {
            _sessions.Remove(
                key
            );
        }
    }

    private void TrimSessions()
    {
        if (
            _sessions.Count <=
                MaximumSessions
        )
        {
            return;
        }

        var overflow =
            _sessions.Count -
            MaximumSessions;

        var oldestKeys =
            _sessions
                .OrderBy(
                    pair =>
                        pair.Value.CreatedAtUtc
                )
                .Take(
                    overflow
                )
                .Select(
                    pair =>
                        pair.Key
                )
                .ToArray();

        foreach (var key in oldestKeys)
        {
            _sessions.Remove(
                key
            );
        }
    }

    private byte[] GetOrCreatePng(
        byte byte0,
        byte byte1
    )
    {
        var cacheKey =
            byte0 |
            byte1 << 8;

        lock (_pngCacheGate)
        {
            if (
                _pngCache.TryGetValue(
                    cacheKey,
                    out var existingNode
                )
            )
            {
                _pngCacheLru.Remove(
                    existingNode
                );

                _pngCacheLru.AddFirst(
                    existingNode
                );

                return existingNode
                    .Value
                    .Bytes;
            }

            var created =
                CreateTransparentPng(
                    DimensionBase +
                    byte0,

                    DimensionBase +
                    byte1
                );

            var createdNode =
                new LinkedListNode<
                    PngCacheEntry
                >(
                    new PngCacheEntry(
                        cacheKey,
                        created
                    )
                );

            _pngCacheLru.AddFirst(
                createdNode
            );

            _pngCache[cacheKey] =
                createdNode;

            if (
                _pngCache.Count >
                    MaximumCachedPngs
            )
            {
                var oldestNode =
                    _pngCacheLru.Last;

                if (oldestNode is not null)
                {
                    _pngCacheLru.RemoveLast();

                    _pngCache.Remove(
                        oldestNode.Value.Key
                    );
                }
            }

            return created;
        }
    }

    public void ClearGeneratedPngCache()
    {
        lock (_pngCacheGate)
        {
            _pngCache.Clear();
            _pngCacheLru.Clear();
        }
    }

    private static int GetChunkCount(
        int packetLength
    )
    {
        return (
            packetLength +
            BytesPerChunk -
            1
        ) /
        BytesPerChunk;
    }

    private static bool IsValidSession(
        string session
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                session
            ) ||
            session.Length >
            MaximumSessionLength
        )
        {
            return false;
        }

        foreach (
            var character in session
        )
        {
            if (
                !char.IsLetterOrDigit(
                    character
                ) &&
                character != '-' &&
                character != '_'
            )
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] CreateTransparentPng(
        int width,
        int height
    )
    {
        using var output =
            new MemoryStream();

        output.Write(
            new byte[]
            {
                137,
                80,
                78,
                71,
                13,
                10,
                26,
                10
            }
        );

        Span<byte> ihdr =
            stackalloc byte[13];

        BinaryPrimitives
            .WriteInt32BigEndian(
                ihdr.Slice(
                    0,
                    4
                ),
                width
            );

        BinaryPrimitives
            .WriteInt32BigEndian(
                ihdr.Slice(
                    4,
                    4
                ),
                height
            );

        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        WritePngChunk(
            output,
            "IHDR",
            ihdr
        );

        var rowLength =
            checked(
                1 +
                width * 4
            );

        using var compressed =
            new MemoryStream();

        using (
            var zlib =
                new ZLibStream(
                    compressed,
                    CompressionLevel.Fastest,
                    leaveOpen:
                        true
                )
        )
        {
            /*
             * Every pixel is transparent, so the scanline is all zeroes.
             * Writing one small row repeatedly avoids allocating up to
             * 294 KB per cache miss (and avoids the Large Object Heap).
             */
            Span<byte> transparentRow =
                stackalloc byte[rowLength];

            transparentRow.Clear();

            for (
                var rowIndex = 0;
                rowIndex < height;
                rowIndex++
            )
            {
                zlib.Write(
                    transparentRow
                );
            }
        }

        WritePngChunk(
            output,
            "IDAT",
            compressed.ToArray()
        );

        WritePngChunk(
            output,
            "IEND",
            ReadOnlySpan<byte>.Empty
        );

        return output.ToArray();
    }

    private static void WritePngChunk(
        Stream output,
        string type,
        ReadOnlySpan<byte> data
    )
    {
        var typeBytes =
            Encoding.ASCII
                .GetBytes(
                    type
                );

        Span<byte> lengthBytes =
            stackalloc byte[4];

        BinaryPrimitives
            .WriteInt32BigEndian(
                lengthBytes,
                data.Length
            );

        output.Write(
            lengthBytes
        );

        output.Write(
            typeBytes
        );

        output.Write(
            data
        );

        Span<byte> crcBytes =
            stackalloc byte[4];

        BinaryPrimitives
            .WriteUInt32BigEndian(
                crcBytes,
                ComputePngCrc(
                    typeBytes,
                    data
                )
            );

        output.Write(
            crcBytes
        );
    }

    private static uint ComputePngCrc(
        ReadOnlySpan<byte> typeBytes,
        ReadOnlySpan<byte> data
    )
    {
        var crc =
            0xFFFFFFFFU;

        crc =
            UpdatePngCrc(
                crc,
                typeBytes
            );

        crc =
            UpdatePngCrc(
                crc,
                data
            );

        return crc ^
            0xFFFFFFFFU;
    }

    private static uint UpdatePngCrc(
        uint crc,
        ReadOnlySpan<byte> bytes
    )
    {
        foreach (
            var value in bytes
        )
        {
            crc ^=
                value;

            for (
                var bit = 0;
                bit < 8;
                bit++
            )
            {
                crc =
                    (
                        crc &
                        1U
                    ) != 0

                        ? 0xEDB88320U ^
                            (
                                crc >>
                                1
                            )

                        : crc >>
                            1;
            }
        }

        return crc;
    }
}

internal sealed record TransportSession(
    DateTimeOffset CreatedAtUtc,
    byte[]? Packet,
    ExceptionDispatchInfo? Failure
);

internal sealed record PngCacheEntry(
    int Key,
    byte[] Bytes
);
