using System;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Implements the oracle tests' availability rules.
/// </summary>
/// <remarks>
/// The RFC 8251 vectors are committed, so a missing vector directory is always a test failure.
/// The libopus oracle binaries are not committed: their absence makes a test return without
/// asserting, unless <c>KILN_AUDIO_REQUIRE_ORACLE=1</c> is set (CI sets it where the oracle
/// builds), in which case the test fails with a named message.
/// </remarks>
internal static class OracleTestSupport
{
    /// <summary>Environment variable that turns a missing oracle into a failure.</summary>
    internal const string RequireOracleVariable = "KILN_AUDIO_REQUIRE_ORACLE";

    /// <summary>True when a missing oracle must fail the test.</summary>
    internal static bool RequireOracle =>
        Environment.GetEnvironmentVariable(RequireOracleVariable) == "1";

    /// <summary>Returns true when the vectors are present; fails the test when they are not.</summary>
    internal static bool VectorsAvailable()
    {
        if (TestVectors.Available)
        {
            return true;
        }

        Assert.Fail("RFC 8251 test vectors not found; run scripts/fetch-test-vectors.sh");
        return false;
    }

    /// <summary>Returns true when the oracle is present; otherwise returns false (or fails).</summary>
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

        if (RequireOracle)
        {
            Assert.Fail("libopus oracle not found; run scripts/build-libopus-oracle.sh");
        }

        return false;
    }
}
