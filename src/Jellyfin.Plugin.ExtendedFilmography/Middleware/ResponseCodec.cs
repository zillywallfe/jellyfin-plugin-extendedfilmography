using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ExtendedFilmography.Middleware;

/// <summary>
/// Encodings the plugin knows how to unwrap and re-wrap.
/// </summary>
public enum BodyEncoding
{
    /// <summary>No compression.</summary>
    Identity = 0,

    /// <summary>gzip.</summary>
    Gzip,

    /// <summary>Brotli.</summary>
    Brotli,

    /// <summary>raw deflate.</summary>
    Deflate,

    /// <summary>Something we do not understand. The body must be passed through untouched.</summary>
    Unsupported,
}

/// <summary>
/// Decompresses and recompresses buffered response bodies.
/// </summary>
/// <remarks>
/// The plugin's middleware sits at the very front of the pipeline, so ASP.NET's response
/// compression middleware runs <em>inside</em> the call to the next delegate and has already
/// compressed the bytes by the time they land in our buffer. To edit the JSON we therefore have
/// to undo that, and put it back the way we found it.
/// </remarks>
public static class ResponseCodec
{
    /// <summary>
    /// Maps a Content-Encoding header value to an encoding.
    /// </summary>
    /// <param name="headerValue">The raw header value, possibly empty.</param>
    /// <returns>The encoding.</returns>
    public static BodyEncoding Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return BodyEncoding.Identity;
        }

        // A stacked encoding ("gzip, br") is legal but never emitted by ASP.NET; refuse it
        // rather than guess.
        if (headerValue.Contains(',', StringComparison.Ordinal))
        {
            return BodyEncoding.Unsupported;
        }

        return headerValue.Trim().ToLowerInvariant() switch
        {
            "identity" => BodyEncoding.Identity,
            "gzip" => BodyEncoding.Gzip,
            "x-gzip" => BodyEncoding.Gzip,
            "br" => BodyEncoding.Brotli,
            "deflate" => BodyEncoding.Deflate,
            _ => BodyEncoding.Unsupported,
        };
    }

    /// <summary>
    /// Decompresses a buffered body.
    /// </summary>
    /// <param name="payload">The raw bytes as written by the pipeline.</param>
    /// <param name="encoding">The encoding reported by the Content-Encoding header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plain bytes.</returns>
    public static async Task<byte[]> DecodeAsync(byte[] payload, BodyEncoding encoding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (encoding == BodyEncoding.Identity || payload.Length == 0)
        {
            return payload;
        }

        using var source = new MemoryStream(payload, writable: false);
        await using var decompressor = CreateDecompressor(source, encoding);
        using var destination = new MemoryStream(payload.Length * 4);
        await decompressor.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    /// <summary>
    /// Recompresses a body with the encoding it originally used.
    /// </summary>
    /// <param name="payload">The plain bytes.</param>
    /// <param name="encoding">The encoding to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes to write to the wire.</returns>
    public static async Task<byte[]> EncodeAsync(byte[] payload, BodyEncoding encoding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (encoding == BodyEncoding.Identity)
        {
            return payload;
        }

        using var destination = new MemoryStream(payload.Length);

        // The compressor must be disposed before the buffer is read, so that it flushes its
        // trailer. Scope it tightly rather than relying on `await using` at method scope.
        await using (var compressor = CreateCompressor(destination, encoding))
        {
            await compressor.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static Stream CreateDecompressor(Stream source, BodyEncoding encoding) => encoding switch
    {
        BodyEncoding.Gzip => new GZipStream(source, CompressionMode.Decompress, leaveOpen: true),
        BodyEncoding.Brotli => new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true),
        BodyEncoding.Deflate => new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Not a decodable encoding."),
    };

    private static Stream CreateCompressor(Stream destination, BodyEncoding encoding) => encoding switch
    {
        BodyEncoding.Gzip => new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true),
        BodyEncoding.Brotli => new BrotliStream(destination, CompressionLevel.Fastest, leaveOpen: true),
        BodyEncoding.Deflate => new DeflateStream(destination, CompressionLevel.Fastest, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Not an encodable encoding."),
    };
}
