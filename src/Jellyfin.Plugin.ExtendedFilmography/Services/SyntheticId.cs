using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Maps TMDb ids to and from the GUIDs the plugin hands out for titles that are not in the
/// library. The mapping is completely stateless and reversible, so nothing has to be persisted
/// and no database row is ever written.
/// </summary>
/// <remarks>
/// Layout of the 16 raw bytes:
/// <list type="table">
///   <item><term>0..3</term><description>Fixed magic prefix, identifies our GUIDs in O(1).</description></item>
///   <item><term>4</term><description>Media kind (1 = movie, 2 = series).</description></item>
///   <item><term>5..8</term><description>TMDb id, big-endian int32.</description></item>
///   <item><term>9..15</term><description>SHA-256 checksum of the above, so a real Jellyfin
///   GUID cannot be mistaken for one of ours.</description></item>
/// </list>
/// </remarks>
public static class SyntheticId
{
    /// <summary>Number of leading bytes reserved for the magic prefix.</summary>
    private const int MagicLength = 4;

    /// <summary>Offset of the checksum within the raw bytes.</summary>
    private const int ChecksumOffset = 9;

    /// <summary>Length of the embedded checksum.</summary>
    private const int ChecksumLength = 7;

    /// <summary>
    /// A prefix chosen to be recognisable in logs and vanishingly unlikely to collide with a
    /// real Jellyfin item id. Do not change: it would invalidate every client-side cache.
    /// </summary>
    private static readonly byte[] Magic = { 0xEF, 0x11, 0x3A, 0x7C };

    /// <summary>
    /// Creates the deterministic GUID for a TMDb title.
    /// </summary>
    /// <param name="kind">Film or series.</param>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <returns>The synthetic item id.</returns>
    public static Guid Create(MediaKind kind, int tmdbId)
    {
        if (kind != MediaKind.Movie && kind != MediaKind.Series)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Movie and Series can be synthesised.");
        }

        Span<byte> raw = stackalloc byte[16];
        Magic.CopyTo(raw);
        raw[4] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(raw.Slice(5, 4), tmdbId);

        ComputeChecksum(raw.Slice(0, ChecksumOffset), raw.Slice(ChecksumOffset, ChecksumLength));

        return new Guid(raw);
    }

    /// <summary>
    /// Determines whether a GUID was produced by <see cref="Create"/>.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <returns><c>true</c> if it is one of ours.</returns>
    public static bool IsSynthetic(Guid id) => TryDecode(id, out _, out _);

    /// <summary>
    /// Reverses <see cref="Create"/>.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <param name="kind">Receives the media kind.</param>
    /// <param name="tmdbId">Receives the TMDb id.</param>
    /// <returns><c>true</c> if the id decoded cleanly.</returns>
    public static bool TryDecode(Guid id, out MediaKind kind, out int tmdbId)
    {
        kind = MediaKind.Unknown;
        tmdbId = 0;

        Span<byte> raw = stackalloc byte[16];
        if (!id.TryWriteBytes(raw))
        {
            return false;
        }

        for (var i = 0; i < MagicLength; i++)
        {
            if (raw[i] != Magic[i])
            {
                return false;
            }
        }

        Span<byte> expected = stackalloc byte[ChecksumLength];
        ComputeChecksum(raw.Slice(0, ChecksumOffset), expected);
        if (!expected.SequenceEqual(raw.Slice(ChecksumOffset, ChecksumLength)))
        {
            return false;
        }

        var rawKind = raw[4];
        if (rawKind != (byte)MediaKind.Movie && rawKind != (byte)MediaKind.Series)
        {
            return false;
        }

        var id32 = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(5, 4));
        if (id32 <= 0)
        {
            return false;
        }

        kind = (MediaKind)rawKind;
        tmdbId = id32;
        return true;
    }

    /// <summary>
    /// Parses a GUID in any of the formats Jellyfin clients send (dashless "N" or dashed "D")
    /// and decodes it in one step.
    /// </summary>
    /// <param name="value">The raw path segment.</param>
    /// <param name="kind">Receives the media kind.</param>
    /// <param name="tmdbId">Receives the TMDb id.</param>
    /// <returns><c>true</c> if the segment was one of our ids.</returns>
    public static bool TryParseAndDecode(ReadOnlySpan<char> value, out MediaKind kind, out int tmdbId)
    {
        kind = MediaKind.Unknown;
        tmdbId = 0;

        // Fast reject: a GUID is 32 chars dashless or 36 dashed.
        if (value.Length != 32 && value.Length != 36)
        {
            return false;
        }

        return Guid.TryParse(value, out var parsed) && TryDecode(parsed, out kind, out tmdbId);
    }

    /// <summary>
    /// Renders a GUID the way the Jellyfin API renders item ids: 32 lowercase hex characters,
    /// no dashes.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The dashless representation.</returns>
    public static string ToApiString(Guid id) => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    private static void ComputeChecksum(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        Span<byte> full = stackalloc byte[32];
        var salted = new byte[input.Length + 5];
        Encoding.ASCII.GetBytes("xfilm").CopyTo(salted, 0);
        input.CopyTo(salted.AsSpan(5));
        SHA256.HashData(salted, full);
        full.Slice(0, destination.Length).CopyTo(destination);
    }
}
