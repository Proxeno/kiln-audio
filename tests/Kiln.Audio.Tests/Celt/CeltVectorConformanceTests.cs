using FluentAssertions;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Celt;

/// <summary>
/// End-to-end conformance of the CELT-only RFC 8251 test vectors against the official
/// reference decodes and against a pinned libopus build used as a black-box oracle.
/// </summary>
/// <remarks>
/// Vectors 01, 07 and 11 are CELT-only, so this lane can decode them end to end. Conformance
/// is the RFC 8251 requirement: <c>opus_compare</c> must report a pass against <c>.dec</c> or
/// the <c>m.dec</c> variant; the sample-by-sample comparison against <c>opus_demo</c> is a
/// stricter diagnostic, reported rather than required by the quality metric.
/// </remarks>
public class CeltVectorConformanceTests
{
    private readonly ITestOutputHelper _output;

    public CeltVectorConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int> CeltOnlyVectors() => new() { 1, 7, 11 };

    [Theory]
    [MemberData(nameof(CeltOnlyVectors))]
    public void Celt_only_vector_is_conformant(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        short[] decoded = CeltVectorDriver.Decode(TestVectors.BitPath(vector));
        decoded.Length.Should().BeGreaterThan(0);

        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        short[] modified = Pcm16.Read(TestVectors.ModifiedDecPath(vector));

        LibOpusOracle.Comparison againstReference = LibOpusOracle.Compare(reference, decoded, 48000, stereo: true);
        LibOpusOracle.Comparison againstModified = LibOpusOracle.Compare(modified, decoded, 48000, stereo: true);
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2}: opus_compare vs .dec {againstReference.Quality:F2} %, vs m.dec {againstModified.Quality:F2} %"));

        short[] libopus = LibOpusOracle.Decode(TestVectors.BitPath(vector), 48000, 2);
        libopus.Length.Should().Be(decoded.Length, "opus_demo must produce the same sample count");

        double signal = 0;
        double noise = 0;
        long maxDifference = 0;
        int firstDifference = -1;
        for (int i = 0; i < decoded.Length; i++)
        {
            long difference = decoded[i] - libopus[i];
            if (difference != 0 && firstDifference < 0)
            {
                firstDifference = i;
            }

            maxDifference = Math.Max(maxDifference, Math.Abs(difference));
            signal += (double)libopus[i] * libopus[i];
            noise += (double)difference * difference;
        }

        double snr = 10 * Math.Log10(signal / Math.Max(noise, 1e-12));
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2}: vs opus_demo max |diff| {maxDifference} LSB, SNR {snr:F2} dB, first diff sample {firstDifference}"));

        // RFC 8251 allows either reference decode; the phase-inverted default matches .dec.
        (againstReference.Passed || againstModified.Passed).Should().BeTrue(
            FormattableString.Invariant(
                $"vector {vector:D2}: opus_compare failed against both references (.dec {againstReference.Quality:F2} %, m.dec {againstModified.Quality:F2} %)"));

        // The sample-by-sample path is nearly exact (only float rounding differs); these bounds
        // catch a structural regression without loosening the quality metric itself.
        maxDifference.Should().BeLessThanOrEqualTo(200);
        snr.Should().BeGreaterThan(40);
    }
}
