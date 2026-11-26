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

    /// <summary>True when the vectors are present on disk.</summary>
    internal static bool Available => RootDirectory.Value is not null;

    /// <summary>The directory holding the vectors.</summary>
    /// <exception cref="InvalidOperationException">The vectors are not present.</exception>
    internal static string Directory => RootDirectory.Value
        ?? throw new InvalidOperationException("RFC 8251 test vectors are not available.");

    /// <summary>Path to <c>testvectorNN.bit</c>, the encoded packets.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string BitPath(int vector) => VectorPath(vector, ".bit");

    /// <summary>Path to <c>testvectorNN.dec</c>, a conformant stereo decode.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string DecPath(int vector) => VectorPath(vector, ".dec");

    /// <summary>Path to <c>testvectorNNm.dec</c>, the second conformant stereo decode.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    internal static string ModifiedDecPath(int vector) => VectorPath(vector, "m.dec");

    private static string VectorPath(int vector, string suffix)
    {
        if (vector is < 1 or > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(vector), vector, "Vector number must be 1 to 12.");
        }

        return Path.Combine(Directory, FormattableString.Invariant($"testvector{vector:D2}{suffix}"));
    }

    private static string? LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "tests", "Kiln.Audio.Tests", "Fixtures", "opus_testvectors");
            if (File.Exists(Path.Combine(candidate, "testvector01.bit"))
                && File.Exists(Path.Combine(candidate, "testvector12.dec")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
