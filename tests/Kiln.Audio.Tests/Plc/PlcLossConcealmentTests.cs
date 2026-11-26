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
/// no NaN or infinity; no clipping or explosion beyond the neighbouring good frames; the
/// concealed spans score better against the clean decode than silence-fill does and within 1 dB
/// of libopus's own concealment; and the decoder state re-converges, proven per vector by
/// requiring our lossy decode in the 160-320 ms window after each loss run to be within 3 dB of
/// our clean decode as closely as libopus's lossy decode is to its own clean decode.
/// </para>
/// <para>
/// Recovery windows are measured against libopus decoded with the same losses, from the first
/// good packet after the run, and are clipped at the start of the next loss run so a later loss
/// does not pollute the measurement. They are 0-20, 20-40, 40-80, 80-160 and 160-320 ms.
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

    private static readonly int[] WindowStartMs = [0, 20, 40, 80, 160];
    private static readonly int[] WindowLengthMs = [20, 20, 40, 80, 160];
    private const int LongWindow = 4;

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
    public void Loss_patterns_re_converge_and_beat_silence_fill()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            _output.WriteLine("libopus oracle not available; skipping.");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("concealed spans (SNR against the clean decode):");
        report.AppendLine("vector,pattern,drops,concealed_ms,ours_db,libopus_db,silence_db,spike_ok");

        var total = new ConcealedTotals();
        var vectorTotals = new Dictionary<string, ConcealedTotals>();
        var recoveries = new Dictionary<string, RecoveryWindows>();
        int scenarios = 0;
        int spikePasses = 0;

        foreach ((int vector, string name) in Cases)
        {
            var packets = PlcTestData.ReadVector(vector);
            short[] oracleClean = LibOpusOracle.Decode(PlcTestData.BuildBitFile(packets), ApiRate, 1);
            short[] oursClean = PlcTestData.ToInt16(PlcTestData.DecodeOursClean(packets, ApiRate));
            var vectorTotal = new ConcealedTotals();
            var recovery = new RecoveryWindows();
            vectorTotals[name] = vectorTotal;
            recoveries[name] = recovery;

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

                var scenario = new ConcealedTotals();
                scenario.AddOurs(oracleClean, oursLossy, concealed);
                scenario.AddLibopus(oracleClean, oracleLossy, concealed);
                scenario.AddSilence(oracleClean, concealed);
                total.Add(scenario);
                vectorTotal.Add(scenario);

                bool spikeOk = CheckNoSpikes(oursLossy, segments, name, pattern);
                if (spikeOk)
                {
                    spikePasses++;
                }

                recovery.Add(oracleLossy, oursLossy, oracleClean, oursClean, segments);

                int concealedSamples = 0;
                foreach ((int start, int end) in concealed)
                {
                    concealedSamples += end - start;
                }

                report.AppendLine(FormattableString.Invariant(
                    $"{name},{pattern},{losses.Count},{concealedSamples * 1000.0 / ApiRate:F1},{scenario.OursDb:F2},{scenario.LibopusDb:F2},{scenario.SilenceDb:F2},{spikeOk}"));
            }
        }

        report.AppendLine();
        report.AppendLine("recovery windows (lossy_db = ours-lossy vs libopus-lossy; no_loss_db = ours-clean vs libopus-clean):");
        report.AppendLine("vector,window_ms,lossy_db,no_loss_db,margin_db,ours_recovery_db,libopus_recovery_db");
        bool longWindowPasses = true;
        foreach ((string name, RecoveryWindows windows) in recoveries)
        {
            for (int w = 0; w < WindowStartMs.Length; w++)
            {
                double lossy = windows.LossyDb(w);
                double noLoss = windows.NoLossDb(w);
                report.AppendLine(FormattableString.Invariant(
                    $"{name},{WindowStartMs[w]}-{WindowStartMs[w] + WindowLengthMs[w]},{lossy:F2},{noLoss:F2},{lossy - noLoss:F2},{windows.OursRecoveryDb(w):F2},{windows.LibopusRecoveryDb(w):F2}"));
            }

            windows.HasData(LongWindow).Should().BeTrue(
                FormattableString.Invariant($"{name}: no complete 160-320 ms window to measure"));

            // HARD gate (coordinator ruling, option (c)): in the 160-320 ms window our lossy
            // decode must be as close to our clean decode as libopus's lossy decode is to its
            // clean decode, within 3 dB. The no-loss-minus-3 dB form (logged above as margin_db)
            // cannot be the gate: for CELT the no-loss cross-SNR is near bit-exact (about 79 dB),
            // while libopus's own lossy decode only recovers to about 67 dB from its clean decode
            // after 320 ms because of its post-filter memory, so that form is capped by the
            // reference's PLC residual rather than by our state.
            if (windows.OursRecoveryDb(LongWindow) < windows.LibopusRecoveryDb(LongWindow) - 3)
            {
                longWindowPasses = false;
            }
        }

        report.AppendLine();
        report.AppendLine("aggregate concealed spans:");
        foreach ((string name, ConcealedTotals sums) in vectorTotals)
        {
            report.AppendLine(FormattableString.Invariant(
                $"{name}: ours {sums.OursDb:F2} dB, libopus {sums.LibopusDb:F2} dB, silence {sums.SilenceDb:F2} dB"));
        }

        report.AppendLine(FormattableString.Invariant(
            $"all: ours {total.OursDb:F2} dB, libopus {total.LibopusDb:F2} dB, silence {total.SilenceDb:F2} dB"));

        _output.WriteLine(report.ToString());
        spikePasses.Should().Be(scenarios, "no concealed run may clip or explode:\n{0}", report);
        total.OursDb.Should().BeGreaterThan(
            total.SilenceDb, FormattableString.Invariant($"concealment must beat silence-fill:\n{report}"));
        total.OursDb.Should().BeGreaterThan(
            total.LibopusDb - 1,
            FormattableString.Invariant($"concealment must be within 1 dB of libopus's own PLC:\n{report}"));
        longWindowPasses.Should().BeTrue(
            FormattableString.Invariant(
                $"in the 160-320 ms window every vector must recover within 3 dB of libopus's own recovery:\n{report}"));
    }

    /// <summary>Accumulated signal and error energies over the concealed spans.</summary>
    private sealed class ConcealedTotals
    {
        private double _signal;
        private double _oursError;
        private double _libopusError;
        private double _silenceError;

        public double OursDb => Snr(_signal, _oursError);

        public double LibopusDb => Snr(_signal, _libopusError);

        public double SilenceDb => Snr(_signal, _silenceError);

        public void AddOurs(short[] reference, short[] test, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < reference.Length; i++)
                {
                    double r = reference[i];
                    _signal += r * r;
                    _oursError += (r - test[i]) * (r - test[i]);
                }
            }
        }

        public void AddLibopus(short[] reference, short[] test, IReadOnlyList<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                for (int i = start; i < end && i < reference.Length; i++)
                {
                    _libopusError += (reference[i] - test[i]) * (reference[i] - test[i]);
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

        public void Add(ConcealedTotals other)
        {
            _signal += other._signal;
            _oursError += other._oursError;
            _libopusError += other._libopusError;
            _silenceError += other._silenceError;
        }

        private static double Snr(double signal, double noise) =>
            noise <= 0 ? 200 : 10 * Math.Log10(signal / noise);
    }

    /// <summary>SNR per recovery window, aggregated over every loss run of a vector.</summary>
    private sealed class RecoveryWindows
    {
        private readonly double[] _signal = new double[WindowStartMs.Length];
        private readonly double[] _lossyError = new double[WindowStartMs.Length];
        private readonly double[] _noLossError = new double[WindowStartMs.Length];
        private readonly double[] _oursRecoverySignal = new double[WindowStartMs.Length];
        private readonly double[] _oursRecoveryError = new double[WindowStartMs.Length];
        private readonly double[] _libopusRecoverySignal = new double[WindowStartMs.Length];
        private readonly double[] _libopusRecoveryError = new double[WindowStartMs.Length];

        public bool HasData(int window) => _signal[window] > 0;

        public double LossyDb(int window) => Snr(_signal[window], _lossyError[window]);

        public double NoLossDb(int window) => Snr(_signal[window], _noLossError[window]);

        public double OursRecoveryDb(int window) =>
            Snr(_oursRecoverySignal[window], _oursRecoveryError[window]);

        public double LibopusRecoveryDb(int window) =>
            Snr(_libopusRecoverySignal[window], _libopusRecoveryError[window]);

        public void Add(
            short[] oracleLossy,
            short[] oursLossy,
            short[] oracleClean,
            short[] oursClean,
            IReadOnlyList<PlcSegment> segments)
        {
            int limit = Math.Min(
                oracleLossy.Length, Math.Min(oursLossy.Length, Math.Min(oracleClean.Length, oursClean.Length)));

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

                // Clip the measurement at the next loss run so a later loss is not counted.
                int nextRun = limit;
                for (int j = index + 1; j < segments.Count; j++)
                {
                    if (segments[j].Concealed)
                    {
                        nextRun = segments[j].StartSample;
                        break;
                    }
                }

                for (int w = 0; w < WindowStartMs.Length; w++)
                {
                    int start = end + (ApiRate * WindowStartMs[w] / 1000);
                    int stop = start + (ApiRate * WindowLengthMs[w] / 1000);

                    // Only a complete window that is not interrupted by the next loss measures
                    // recovery; a partial one would blend a new loss into the result.
                    if (stop > nextRun || stop > limit)
                    {
                        continue;
                    }

                    for (int i = start; i < stop; i++)
                    {
                        double r = oracleLossy[i];
                        _signal[w] += r * r;
                        _lossyError[w] += (r - oursLossy[i]) * (r - oursLossy[i]);
                        _noLossError[w] += (oracleClean[i] - oursClean[i]) * (oracleClean[i] - oursClean[i]);

                        double oursSignal = oursClean[i];
                        _oursRecoverySignal[w] += oursSignal * oursSignal;
                        _oursRecoveryError[w] += (oursSignal - oursLossy[i]) * (oursSignal - oursLossy[i]);

                        double libSignal = oracleClean[i];
                        _libopusRecoverySignal[w] += libSignal * libSignal;
                        _libopusRecoveryError[w] += (libSignal - oracleLossy[i]) * (libSignal - oracleLossy[i]);
                    }
                }

                index++;
            }
        }

        private static double Snr(double signal, double noise) =>
            noise <= 0 ? 200 : 10 * Math.Log10(signal / noise);
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
