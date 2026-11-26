using System;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Locates the RFC 8251 Opus test vectors, which are fetched (never committed) by
/// <c>scripts/fetch-test-vectors.sh</c> into <c>tests/Kiln.Audio.Tests/Fixtures/opus_testvectors</c>.
/// </summary>
internal static class TestVectors
{
    /// <summary>Number of test vectors in the RFC 8251 set.</summary>
    internal const int Count = 12;

    private static readonly Lazy<string?> RootDirectory = new(LocateRoot);
    private static readonly Lazy<string?> BitDirectory = new(LocateBitDirectory);

    /// <summary>True when the vectors, including the generated decodes, are present on disk.</summary>
    internal static bool Available => RootDirectory.Value is not null;

    /// <summary>The directory holding the vectors, including the generated decodes.</summary>
    /// <exception cref="InvalidOperationException">The vectors are not present.</exception>
    internal static string Directory => RootDirectory.Value
        ?? throw new InvalidOperationException("RFC 8251 test vectors are not available.");

    /// <summary>
    /// Path to <c>testvectorNN.bit</c>, the encoded packets. The <c>.bit</c> files are committed,
    /// so this path is available even when the generated <c>.dec</c> decodes are not.
    /// </summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string BitPath(int vector) => VectorPath(BitDirectory.Value
        ?? throw new InvalidOperationException("RFC 8251 test vectors are not available."), vector, ".bit");

    /// <summary>Path to <c>testvectorNN.dec</c>, a conformant stereo decode.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string DecPath(int vector) => VectorPath(Directory, vector, ".dec");

    /// <summary>Path to <c>testvectorNNm.dec</c>, the second conformant stereo decode.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string ModifiedDecPath(int vector) => VectorPath(Directory, vector, "m.dec");

    private static string VectorPath(string root, int vector, string suffix)
    {
        if (vector is < 1 or > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(vector), vector, "Vector number must be 1 to 12.");
        }

        return Path.Combine(root, FormattableString.Invariant($"testvector{vector:D2}{suffix}"));
    }

    private static string? LocateRoot()
    {
        // The oracle tests also need the generated decodes.
        return Locate("testvector12.dec");
    }

    private static string? LocateBitDirectory()
    {
        // The .bit files are committed; the .dec decodes are generated on demand.
        return Locate("testvector01.bit");
    }

    private static string? Locate(string marker)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "tests", "Kiln.Audio.Tests", "Fixtures", "opus_testvectors");
            if (File.Exists(Path.Combine(candidate, marker)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
