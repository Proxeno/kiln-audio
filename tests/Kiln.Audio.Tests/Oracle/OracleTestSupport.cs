using System;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Implements the oracle tests' skip rule. Absent fixtures make a test return without asserting,
/// so a clean checkout still passes <c>dotnet test</c>. When <c>KILN_AUDIO_REQUIRE_ORACLE=1</c>
/// is set (CI sets it where the oracle builds), absence instead fails the test with a named
/// message that says how to produce the missing pieces.
/// </summary>
internal static class OracleTestSupport
{
    /// <summary>Environment variable that turns absence into a failure.</summary>
    internal const string RequireOracleVariable = "KILN_AUDIO_REQUIRE_ORACLE";

    /// <summary>True when absence of the fixtures or oracle must fail the test.</summary>
    internal static bool RequireOracle =>
        Environment.GetEnvironmentVariable(RequireOracleVariable) == "1";

    /// <summary>Returns true when the test may proceed; otherwise returns false (or fails).</summary>
    internal static bool VectorsAvailable()
    {
        if (TestVectors.Available)
        {
            return true;
        }

        FailIfRequired("RFC 8251 test vectors not found; run scripts/fetch-test-vectors.sh");
        return false;
    }

    /// <summary>Returns true when the oracle and vectors are present; otherwise returns false (or fails).</summary>
    internal static bool OracleAvailable()
    {
        if (!VectorsAvailable())
        {
            return false;
        }

        if (LibOpusOracle.Available)
        {
            return true;
        }

        FailIfRequired("libopus oracle not found; run scripts/build-libopus-oracle.sh");
        return false;
    }

    private static void FailIfRequired(string message)
    {
        if (RequireOracle)
        {
            Assert.Fail(message);
        }
    }
}
