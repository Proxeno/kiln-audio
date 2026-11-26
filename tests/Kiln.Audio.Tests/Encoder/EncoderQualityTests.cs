using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>G2: aggregate segmental SNR against libopus on the RFC 8251 corpus.</summary>
/// <remarks>
/// Each case covers one (bitrate, complexity) pair over the whole corpus, which keeps every test
/// under the runner's per-test timeout. The hard gate is asserted on the complexity-0 cases: the
/// aggregate segmental SNR must be at least libopus's. The complexity-5 and complexity-10 cases
/// are measurement only. Results are appended to a report file under the system temp directory.
/// </remarks>
public class EncoderQualityTests
{
    private static readonly Lazy<IReadOnlyList<(int Vector, short[] Mono)>> Corpus = new(LoadLimitedCorpus);

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        foreach (int bitrate in new[] { 24000, 32000 })
        {
            foreach (int complexity in new[] { 0, 5, 10 })
            {
                data.Add(bitrate, complexity);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_quality(int bitrate, int complexity)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        double oursTotal = 0;
        double libopusTotal = 0;
        double oursQuality = 0;
        double libopusQuality = 0;
        long segments = 0;
        int vectors = 0;

        bool measureQuality = true;
        foreach ((int vector, short[] mono) in Corpus.Value)
        {
            (double ours, long count) = Measure(mono, bitrate, complexity, measureQuality, out double ourQuality);
            (double theirs, _) = MeasureLibopus(mono, bitrate, complexity, measureQuality, out double theirQuality);

            oursTotal += ours * count;
            libopusTotal += theirs * count;
            segments += count;
            oursQuality += ourQuality;
            libopusQuality += theirQuality;
            vectors++;
        }

        double oursMean = oursTotal / segments;
        double libopusMean = libopusTotal / segments;
        SaveReport(bitrate, complexity, oursMean, libopusMean, oursQuality / vectors, libopusQuality / vectors, segments);

        if (complexity == 0)
        {
            oursMean.Should().BeGreaterThanOrEqualTo(
                libopusMean,
                $"G2 requires ours >= libopus at complexity 0 at {bitrate} bps " +
                $"(ours {oursMean:F3} dB, libopus {libopusMean:F3} dB)");
        }
    }

    private static (double Mean, long Segments) Measure(
        short[] mono, int bitrate, int complexity, bool measureQuality, out double quality)
    {
        var packets = EncoderCorpus.EncodeWithLibrary(mono, bitrate, OpusFrameDuration.Ms20, complexity);
        var bit = new MemoryStream();
        EncoderCorpus.WriteBitFileTo(bit, packets);
        (short[] decoded, string diagnostics) = EncoderOracle.Decode(bit.ToArray());
        EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(diagnostics);
        (double mean, long segments) = EncoderCorpus.SegmentalSnr(mono, decoded, EncoderCorpus.StreamDelay);
        quality = measureQuality ? CompareMono(mono, decoded) : 0;
        return (mean, segments);
    }

    private static (double Mean, long Segments) MeasureLibopus(
        short[] mono, int bitrate, int complexity, bool measureQuality, out double quality)
    {
        byte[] bit = EncoderOracle.EncodeRestrictedLowDelay(mono, bitrate, 20, complexity);
        (short[] decoded, string diagnostics) = EncoderOracle.Decode(bit);
        EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(diagnostics);

        // opus_demo encodes one trailing zero-padded frame at end of file; drop it so both
        // codecs are compared over the same span.
        if (decoded.Length > mono.Length)
        {
            decoded = decoded.AsSpan(0, mono.Length).ToArray();
        }

        (double mean, long segments) = EncoderCorpus.SegmentalSnr(mono, decoded, EncoderCorpus.StreamDelay);
        quality = measureQuality ? CompareMono(mono, decoded) : 0;
        return (mean, segments);
    }

    /// <summary>
    /// Runs <c>opus_compare</c> in mono mode over the first ten seconds of the signal, after
    /// removing the codec's 2.5 ms stream delay.
    /// </summary>
    /// <remarks>
    /// The reference is the <c>.dec</c> decode, which is itself delayed by the codec that produced
    /// it; encoding and decoding it again adds one more stream delay. Aligning by
    /// <see cref="EncoderCorpus.StreamDelay"/> is what makes the spectral comparison meaningful.
    /// <c>opus_compare</c> always reads its reference file as stereo, so the mono reference is
    /// duplicated to two identical channels. The tool's naive DFT is O(audio length); ten seconds
    /// per vector keeps the test runnable while still covering every corpus file.
    /// </remarks>
    private static double CompareMono(short[] reference, short[] test)
    {
        const int excerpt = 10 * 48000;
        int length = Math.Min(Math.Min(reference.Length, test.Length) - EncoderCorpus.StreamDelay, excerpt);
        short[] referenceSlice = reference.AsSpan(0, length).ToArray();
        short[] testSlice = test.AsSpan(EncoderCorpus.StreamDelay, length).ToArray();
        short[] referenceStereo = EncoderCorpus.DuplicateToStereo(referenceSlice);
        return LibOpusOracle.Compare(referenceStereo, testSlice, 48000, stereo: false).Quality;
    }

    private static void SaveReport(
        int bitrate, int complexity, double ours, double libopus, double ourQuality, double theirQuality, long segments)
    {
        string path = Path.Combine(Path.GetTempPath(), "kiln-encoder-quality.txt");
        string line =
            $"ENCODER {bitrate} c{complexity}: ours {ours:F3} dB, libopus {libopus:F3} dB, " +
            $"gap {ours - libopus:+0.000;-0.000} dB, quality ours {ourQuality:F1}% libopus {theirQuality:F1}%, " +
            $"segments {segments}\n";
        File.AppendAllText(path, line);
    }

    private static IReadOnlyList<(int Vector, short[] Mono)> LoadLimitedCorpus()
    {
        var corpus = EncoderCorpus.Load();
        string? limit = Environment.GetEnvironmentVariable("KILN_ENCODER_CORPUS_LIMIT");
        if (limit is not null
            && int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            && count > 0)
        {
            return corpus.Take(Math.Min(count, corpus.Count)).ToArray();
        }

        return corpus;
    }
}
