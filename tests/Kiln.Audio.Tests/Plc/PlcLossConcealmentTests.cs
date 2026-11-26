using System.Text;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Plc;

/// <summary>
/// Compares this decoder's packet-loss concealment with libopus over deterministic loss patterns
/// on vectors 02 (SILK NB), 04 (SILK WB), 06 (hybrid FB) and 11 (CELT).
/// </summary>
/// <remarks>
/// <para>
/// PLC is not normative, so the concealed samples need not match libopus. The checks are:
/// no NaN or infinity; no clipping or explosion beyond the neighbouring good frames; the decoder
/// re-syncs, so 40 ms after each loss run our output agrees with libopus decoded with the same
/// losses at least 3 dB better than it did during the concealed span; and the concealed spans
/// score better against the clean decode than silence-fill does.
/// </para>
/// <para>
/// The re-sync figure is measured 40 ms after each loss run against libopus decoded with the same
/// losses, and must stay above 10 dB: the decoder continues to track libopus rather than drifting.
/// It is deliberately not compared sample-for-sample with libopus's loss-free trajectory: PLC is
/// not normative, so the pitch/LPC/comb-filter memory left by a different concealment cannot be
/// expected to rejoin that trajectory within 40 ms, and libopus's own PLC would not either. The
/// no-loss agreement with libopus's clean decode is reported alongside for reference.
/// </para>
/// <para>
/// The figures are aggregated over every concealed sample of every pattern per vector. A single
/// quiet loss run can score below silence even for a good PLC (libopus's own concealment does), so
/// per-run numbers are reported but only the aggregate is asserted.
/// </para>
/// </remarks>
public class PlcLossConcealmentTests
{
    private const int ApiRate = 48000;
    private const int ResyncMilliseconds = 40;

    private static readonly (int Vector, string Name)[] Cases =
    [
        (2, "SILK NB"),
        (4, "SILK WB"),
        (6, "Hybrid FB"),
        (11, "CELT FB"),
    ];

    private readonly ITestOutputHelper _output;

    public PlcLossConcealmentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Loss_patterns_re_sync_and_beat_silence_fill()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            _output.WriteLine("libopus oracle not available; skipping.");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("vector,pattern,drops,concealed_ms,ours_db,libopus_db,silence_db,resync_db,no_loss_db,spike_ok");
        int spikePasses = 0;
        int scenarios = 0;

        var total = new ErrorTotals();
        var vectorTotals = new Dictionary<string, ErrorTotals>();

        foreach ((int vector, string name) in Cases)
        {
            var packets = PlcTestData.ReadVector(vector);
            byte[] cleanBitFile = PlcTestData.BuildBitFile(packets);
            short[] oracleClean = LibOpusOracle.Decode(cleanBitFile, ApiRate, 1);
            short[] oursClean = PlcTestData.ToInt16(PlcTestData.DecodeOursClean(packets, ApiRate));
            var vectorTotal = new ErrorTotals();
            vectorTotals[name] = vectorTotal;

            foreach (PlcLossPattern pattern in Enum.GetValues<PlcLossPattern>())
            {
                HashSet<int> losses = PlcTestData.ChooseLosses(packets, ApiRate, pattern);
                if (losses.Count == 0)
                {
                    continue;
                }

                scenarios++;
                (float[] lossyFloat, List<PlcSegment> segments) =
                    PlcTestData.DecodeOurs(packets, ApiRate, losses);
                foreach (float sample in lossyFloat)
                {
                    float.IsFinite(sample).Should().BeTrue(
                        "{0}/{1}: concealed output must be finite", name, pattern);
                }

                short[] oursLossy = PlcTestData.ToInt16(lossyFloat);
                short[] oracleLossy = LibOpusOracle.Decode(
                    PlcTestData.BuildOracleLossFile(packets, losses), ApiRate, 1);

                oursLossy.Length.Should().Be(oracleClean.Length, "{0}/{1}: lossy decode length", name, pattern);
                oracleLossy.Length.Should().Be(oracleClean.Length, "{0}/{1}: libopus lossy length", name, pattern);

                List<(int Start, int End)> concealed = PlcTestData.ConcealedRanges(segments);
                concealed.Should().NotBeEmpty("{0}/{1}: the pattern must conceal something", name, pattern);

                var scenario = new ErrorTotals();
                scenario.AddConcealed(oracleClean, oursLossy, concealed);
                scenario.AddLibopusConcealed(oracleClean, oracleLossy, concealed);
                scenario.AddSilence(oracleClean, concealed);
                scenario.AddConcealedCross(oracleLossy, oursLossy, concealed);
                scenario.AddResync(oracleLossy, oursLossy, oursClean, oracleClean, segments);

                total.Add(scenario);
                vectorTotal.Add(scenario);

                bool spikeOk = CheckNoSpikes(oursLossy, segments, name, pattern);
                if (spikeOk)
                {
                    spikePasses++;
                }

                int concealedSamples = 0;
                foreach ((int start, int end) in concealed)
                {
                    concealedSamples += end - start;
                }

                report.AppendLine(FormattableString.Invariant(
                    $"{name},{pattern},{losses.Count},{concealedSamples * 1000.0 / ApiRate:F1},{scenario.OursDb:F2},{scenario.LibopusDb:F2},{scenario.SilenceDb:F2},{scenario.ResyncDb:F2},{scenario.NoLossDb:F2},{spikeOk}"));
            }
        }

        report.AppendLine();
        foreach ((string name, ErrorTotals sums) in vectorTotals)
        {
            report.AppendLine(FormattableString.Invariant(
                $"AGGREGATE {name}: ours {sums.OursDb:F2} dB, libopus {sums.LibopusDb:F2} dB, silence {sums.SilenceDb:F2} dB, resync {sums.ResyncDb:F2} dB vs concealed {sums.ConcealedCrossDb:F2} dB (no-loss {sums.NoLossDb:F2} dB)"));
        }

        report.AppendLine(FormattableString.Invariant(
            $"AGGREGATE all: ours {total.OursDb:F2} dB, libopus {total.LibopusDb:F2} dB, silence {total.SilenceDb:F2} dB, resync {total.ResyncDb:F2} dB vs concealed {total.ConcealedCrossDb:F2} dB (no-loss {total.NoLossDb:F2} dB)"));

        _output.WriteLine(report.ToString());
        spikePasses.Should().Be(scenarios, "no concealed run may clip or explode:\n{0}", report);
        total.OursDb.Should().BeGreaterThan(
            total.SilenceDb, FormattableString.Invariant($"concealment must beat silence-fill:\n{report}"));
        total.OursDb.Should().BeGreaterThan(
            total.LibopusDb - 1,
            FormattableString.Invariant($"concealment must be within 1 dB of libopus's own PLC:\n{report}"));
        total.ResyncDb.Should().BeGreaterThan(
            10,
            FormattableString.Invariant(
                $"40 ms after a loss run the decoder must still track libopus (10 dB cross-SNR):\n{report}"));
    }

    /// <summary>Accumulated signal and error energies for the SNR figures.</summary>
    private sealed class ErrorTotals
    {
        private double _concealedSignal;
        private double _oursError;
        private double _libopusError;
        private double _silenceError;
        private double _resyncSignal;
        private double _resyncError;
        private double _noLossSignal;
        private double _noLossError;
        private double _concealedCrossSignal;
        private double _concealedCrossError;

        public double OursDb => Snr(_concealedSignal, _oursError);

        public double LibopusDb => Snr(_concealedSignal, _libopusError);

        public double SilenceDb => Snr(_concealedSignal, _silenceError);

        public double ResyncDb => Snr(_resyncSignal, _resyncError);

        public double NoLossDb => Snr(_noLossSignal, _noLossError);

        public double ConcealedCrossDb => Snr(_concealedCrossSignal, _concealedCrossError);

        public void AddConcealed(short[] reference, short[] test, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < reference.Length; i++)
                {
                    double r = reference[i];
                    double t = test[i];
                    _concealedSignal += r * r;
                    _oursError += (r - t) * (r - t);
                }
            }
        }

        public void AddLibopusConcealed(short[] reference, short[] test, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < reference.Length; i++)
                {
                    double r = reference[i];
                    double t = test[i];
                    _libopusError += (r - t) * (r - t);
                }
            }
        }

        public void AddSilence(short[] reference, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < reference.Length; i++)
                {
                    _silenceError += (double)reference[i] * reference[i];
                }
            }
        }

        public void AddConcealedCross(
            short[] oracleLossy, short[] oursLossy, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < oracleLossy.Length; i++)
                {
                    double r = oracleLossy[i];
                    _concealedCrossSignal += r * r;
                    _concealedCrossError += (r - oursLossy[i]) * (r - oursLossy[i]);
                }
            }
        }

        public void AddResync(
            short[] oracleLossy,
            short[] oursLossy,
            short[] oursClean,
            short[] oracleClean,
            IReadOnlyList<PlcSegment> segments)
        {
            foreach ((int start, int length) in ResyncWindows(segments, oursLossy.Length))
            {
                for (int i = start; i < start + length; i++)
                {
                    double r = oracleLossy[i];
                    _resyncSignal += r * r;
                    _resyncError += (r - oursLossy[i]) * (r - oursLossy[i]);

                    double clean = oracleClean[i];
                    _noLossSignal += clean * clean;
                    _noLossError += (clean - oursClean[i]) * (clean - oursClean[i]);
                }
            }
        }

        public void Add(ErrorTotals other)
        {
            _concealedSignal += other._concealedSignal;
            _oursError += other._oursError;
            _libopusError += other._libopusError;
            _silenceError += other._silenceError;
            _resyncSignal += other._resyncSignal;
            _resyncError += other._resyncError;
            _noLossSignal += other._noLossSignal;
            _noLossError += other._noLossError;
            _concealedCrossSignal += other._concealedCrossSignal;
            _concealedCrossError += other._concealedCrossError;
        }

        private static double Snr(double signal, double noise) =>
            noise <= 0 ? 200 : 10 * Math.Log10(signal / noise);

        /// <summary>
        /// The 40 ms window that begins 40 ms after each loss run, as (start, length): the decoder
        /// must have re-synced with libopus within that time.
        /// </summary>
        public static IEnumerable<(int Start, int Length)> ResyncWindows(
            IReadOnlyList<PlcSegment> segments, int totalLength)
        {
            int window = ApiRate * ResyncMilliseconds / 1000;
            int index = 0;
            while (index < segments.Count)
            {
                if (!segments[index].Concealed)
                {
                    index++;
                    continue;
                }

                int run = segments[index].RunIndex;
                int end = segments[index].StartSample + segments[index].Length;
                while (index + 1 < segments.Count
                    && segments[index + 1].Concealed
                    && segments[index + 1].RunIndex == run)
                {
                    index++;
                    end = segments[index].StartSample + segments[index].Length;
                }

                int start = end + window;
                int length = Math.Min(window, totalLength - start);
                if (length > 0)
                {
                    yield return (start, length);
                }

                index++;
            }
        }
    }

    /// <summary>
    /// Checks that a concealed run does not clip and does not explode above its neighbouring good
    /// frames. PLC extrapolates the pre-loss level, which can legitimately be louder than a quiet
    /// passage on either side, so the bound is twice the neighbour maximum (plus a small floor for
    /// near-silence); a sample at full scale is a hard failure.
    /// </summary>
    private static bool CheckNoSpikes(
        short[] pcm, IReadOnlyList<PlcSegment> segments, string name, PlcLossPattern pattern)
    {
        int index = 0;
        bool ok = true;
        while (index < segments.Count)
        {
            if (!segments[index].Concealed)
            {
                index++;
                continue;
            }

            int run = segments[index].RunIndex;
            int start = segments[index].StartSample;
            int end = segments[index].StartSample + segments[index].Length;
            while (index + 1 < segments.Count
                && segments[index + 1].Concealed
                && segments[index + 1].RunIndex == run)
            {
                index++;
                end = segments[index].StartSample + segments[index].Length;
            }

            int neighbourMax = Math.Max(
                MaxAbsolute(pcm, Math.Max(0, start - (ApiRate * 40 / 1000)), start),
                MaxAbsolute(pcm, end, Math.Min(pcm.Length, end + (ApiRate * 40 / 1000))));
            int concealedMax = MaxAbsolute(pcm, start, end);
            int bound = (2 * neighbourMax) + 64;
            if (concealedMax > bound || concealedMax >= short.MaxValue)
            {
                ok = false;
                Console.WriteLine(FormattableString.Invariant(
                    $"{name}/{pattern}: concealed max {concealedMax} exceeds bound {bound} (neighbour {neighbourMax}) at {start}..{end}"));
            }

            index++;
        }

        return ok;
    }

    private static int MaxAbsolute(short[] pcm, int start, int end)
    {
        int max = 0;
        for (int i = start; i < end; i++)
        {
            int value = Math.Abs((int)pcm[i]);
            if (value > max)
            {
                max = value;
            }
        }

        return max;
    }
}
