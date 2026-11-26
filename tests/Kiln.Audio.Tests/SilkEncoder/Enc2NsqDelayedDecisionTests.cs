using FluentAssertions;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Checks the delayed-decision noise shaping quantizer (RFC 6716 §5.2.3.8, Appendix A
/// <c>silk/NSQ_del_dec.c</c>): the vector candidate kernel against its scalar twin, and the
/// trellis against a minimal greedy quantizer on the same synthetic signals.
/// </summary>
public class Enc2NsqDelayedDecisionTests
{
    private const int SubframeLength = 80;
    private const int SubframeCount = 4;
    private const int FrameLength = SubframeLength * SubframeCount;
    private const int LtpMemoryLength = 100;
    private const int PredictOrder = 10;
    private const int ShapingOrder = 10;
    private const int OffsetQ10 = 100; // Inactive/unvoiced, low offset (Table 53).

    /// <summary>The trellis width follows the encoder complexity (Appendix A <c>silk_setup_complexity</c>).</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 4)]
    [InlineData(10, 4)]
    public void States_follow_complexity(int complexity, int expectedStates)
    {
        SilkNsqDelayedDecision.StatesForComplexity(complexity).Should().Be(expectedStates);
    }

    /// <summary>Out-of-range complexities are rejected rather than silently clamped.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Complexity_out_of_range_throws(int complexity)
    {
        Action act = () => SilkNsqDelayedDecision.StatesForComplexity(complexity);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>The vector path is bit-exact with the scalar candidate evaluation.</summary>
    [Fact]
    public void Vector_candidate_evaluation_is_bit_exact()
    {
        var random = new Random(20261126);
        Span<int> residual = stackalloc int[4];
        Span<int> seeds = stackalloc int[4];
        Span<int> scalarQ1 = stackalloc int[4];
        Span<int> scalarQ2 = stackalloc int[4];
        Span<int> scalarRd1 = stackalloc int[4];
        Span<int> scalarRd2 = stackalloc int[4];
        Span<int> vectorQ1 = stackalloc int[4];
        Span<int> vectorQ2 = stackalloc int[4];
        Span<int> vectorRd1 = stackalloc int[4];
        Span<int> vectorRd2 = stackalloc int[4];
        int[] offsets = [32, 100, 240];
        int[] lambdas = [0, 1, 256, 1024, 1500];

        for (int trial = 0; trial < 20000; trial++)
        {
            int count = 1 + random.Next(4);
            for (int k = 0; k < count; k++)
            {
                residual[k] = random.Next(4) switch
                {
                    0 => random.Next(-(31 << 10), 30 << 10),
                    1 => (random.Next(-4, 5) << 10) + random.Next(-100, 101),
                    2 => (31 << 10) - random.Next(0, 3),
                    _ => -(31 << 10) + random.Next(0, 3),
                };
                seeds[k] = random.Next(-1000, 1000);
            }

            int offset = offsets[random.Next(offsets.Length)];
            int lambda = lambdas[random.Next(lambdas.Length)];
            SilkNsqDelayedDecisionKernel.EvaluateScalar(
                residual, seeds, count, offset, lambda, scalarQ1, scalarQ2, scalarRd1, scalarRd2);
            SilkNsqDelayedDecisionKernel.EvaluateSimd(
                residual, seeds, count, offset, lambda, vectorQ1, vectorQ2, vectorRd1, vectorRd2);

            for (int k = 0; k < count; k++)
            {
                vectorQ1[k].Should().Be(scalarQ1[k]);
                vectorQ2[k].Should().Be(scalarQ2[k]);
                vectorRd1[k].Should().Be(scalarRd1[k]);
                vectorRd2[k].Should().Be(scalarRd2[k]);
            }
        }
    }

    /// <summary>
    /// The trellis never does worse than the greedy quantizer, and with one state it makes
    /// exactly the greedy choices.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(4242)]
    public void Trellis_weighted_error_never_exceeds_greedy(int seed)
    {
        var random = new Random(seed);
        short[] prediction = CreatePredictionCoefficients();
        short[] shaping = CreateShapingCoefficients();
        int[] gains = [65536, 65536, 65536, 65536];
        int[] xQ3 = new int[FrameLength];
        int[] pitchLags = new int[SubframeCount];
        for (int i = 0; i < FrameLength; i++)
        {
            xQ3[i] = random.Next(-240, 241);
        }

        (sbyte[] greedyPulses, int greedyCost) =
            GreedyQuantize(xQ3, prediction, shaping, gains, pitchLags);

        var singleState = new SilkNsqDelayedDecision();
        sbyte[] singlePulses = new sbyte[FrameLength];
        short[] singleXq = new short[FrameLength];
        singleState.Run(
            CreateParameters(xQ3, prediction, shaping, gains, pitchLags, seed: 0),
            stateCount: 1, singlePulses, singleXq, out int singleSeed);
        singlePulses.Should().Equal(greedyPulses, "one state makes the greedy decision");
        singleState.LastCostQ10.Should().Be(greedyCost);

        var trellis = new SilkNsqDelayedDecision();
        sbyte[] trellisPulses = new sbyte[FrameLength];
        short[] trellisXq = new short[FrameLength];
        trellis.Run(
            CreateParameters(xQ3, prediction, shaping, gains, pitchLags, seed: 0),
            stateCount: 4, trellisPulses, trellisXq, out int trellisSeed);

        trellis.LastCostQ10.Should().BeLessThanOrEqualTo(greedyCost);
        WeightedError(trellisPulses, trellisSeed, xQ3, prediction, shaping, gains)
            .Should().BeLessThanOrEqualTo(WeightedError(greedyPulses, singleSeed, xQ3, prediction, shaping, gains));
    }

    /// <summary>A voiced frame runs through the LTP and re-whitening paths without changing state invariants.</summary>
    [Fact]
    public void Voiced_frame_runs_through_the_ltp_path()
    {
        var random = new Random(77);
        int[] xQ3 = new int[FrameLength];
        for (int i = 0; i < FrameLength; i++)
        {
            xQ3[i] = random.Next(-240, 241);
        }

        int[] pitchLags = [40, 41, 40, 41];
        short[] ltp = new short[SilkNsqDelayedDecision.LtpOrder * SubframeCount];
        for (int i = 0; i < ltp.Length; i++)
        {
            ltp[i] = (short)random.Next(-8000, 8001);
        }

        var quantizer = new SilkNsqDelayedDecision();
        sbyte[] pulses = new sbyte[FrameLength];
        short[] xq = new short[FrameLength];
        var parameters = new SilkNsqParameters(
            xQ3, CreatePredictionCoefficients(), ltp, CreateShapingCoefficients(),
            [2000, 2000, 2000, 2000], [0, 0, 0, 0], [2048, 2048, 2048, 2048],
            [65536, 65536, 65536, 65536], pitchLags,
            signalType: 2, quantizationOffsetType: 0, lambdaQ10: 1024, ltpScaleQ14: 15565,
            seed: 0, nlsfInterpolationFactor: 4,
            subframeCount: SubframeCount, subframeLength: SubframeLength,
            ltpMemoryLength: LtpMemoryLength, predictLpcOrder: PredictOrder,
            shapingLpcOrder: ShapingOrder, warpingQ16: 0);

        quantizer.Run(parameters, stateCount: 4, pulses, xq, out _);
        quantizer.LagPrev.Should().Be(pitchLags[SubframeCount - 1]);
        pulses.Should().Contain(p => p != 0);

        // A second frame with different gains exercises the state rescaling and history carry.
        int[] secondGains = [70000, 60000, 50000, 40000];
        var secondFrame = new SilkNsqParameters(
            xQ3, CreatePredictionCoefficients(), ltp, CreateShapingCoefficients(),
            [2000, 2000, 2000, 2000], [0, 0, 0, 0], [2048, 2048, 2048, 2048],
            secondGains, pitchLags,
            signalType: 2, quantizationOffsetType: 1, lambdaQ10: 1024, ltpScaleQ14: 15565,
            seed: 1, nlsfInterpolationFactor: 2,
            subframeCount: SubframeCount, subframeLength: SubframeLength,
            ltpMemoryLength: LtpMemoryLength, predictLpcOrder: PredictOrder,
            shapingLpcOrder: ShapingOrder, warpingQ16: 0);

        quantizer.Run(secondFrame, stateCount: 4, pulses, xq, out int seed);
        seed.Should().BeInRange(0, 3);
    }

    /// <summary>Quantizing a frame allocates nothing after construction.</summary>
    [Fact]
    public void Run_allocates_nothing()
    {
        var random = new Random(5);
        int[] xQ3 = new int[FrameLength];
        for (int i = 0; i < FrameLength; i++)
        {
            xQ3[i] = random.Next(-240, 241);
        }

        short[] prediction = CreatePredictionCoefficients();
        short[] shaping = CreateShapingCoefficients();
        int[] gains = [65536, 65536, 65536, 65536];
        int[] pitchLags = new int[SubframeCount];
        var quantizer = new SilkNsqDelayedDecision();
        sbyte[] pulses = new sbyte[FrameLength];
        short[] xq = new short[FrameLength];
        SilkNsqParameters parameters = CreateParameters(xQ3, prediction, shaping, gains, pitchLags, 0);

        for (int i = 0; i < 10; i++)
        {
            quantizer.Run(parameters, 4, pulses, xq, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            quantizer.Run(parameters, 4, pulses, xq, out _);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    private static SilkNsqParameters CreateParameters(
        int[] xQ3, short[] prediction, short[] shaping, int[] gains, int[] pitchLags, int seed) =>
        new(
            xQ3, prediction, new short[SilkNsqDelayedDecision.LtpOrder * SubframeCount], shaping,
            new int[SubframeCount], new int[SubframeCount], [2048, 2048, 2048, 2048],
            gains, pitchLags,
            signalType: 1, quantizationOffsetType: 0, lambdaQ10: 1024, ltpScaleQ14: 15565,
            seed, nlsfInterpolationFactor: 4,
            subframeCount: SubframeCount, subframeLength: SubframeLength,
            ltpMemoryLength: LtpMemoryLength, predictLpcOrder: PredictOrder,
            shapingLpcOrder: ShapingOrder, warpingQ16: 0);

    // The reference selects A_Q12 from index ((k >> 1) | (1 - interp_flag)) * MAX_LPC_ORDER.
    // With interpolation disabled that is index 16 for every subframe.
    private static short[] CreatePredictionCoefficients()
    {
        short[] coefficients = new short[2 * SilkNsqDelayedDecision.MaxPredictionOrder];
        coefficients[16] = 3000;
        coefficients[17] = -1000;
        coefficients[18] = 400;
        coefficients[19] = -150;
        coefficients[20] = 60;
        return coefficients;
    }

    private static short[] CreateShapingCoefficients()
    {
        short[] coefficients = new short[SilkNsqDelayedDecision.MaxShapingOrder * SubframeCount];
        for (int subframe = 0; subframe < SubframeCount; subframe++)
        {
            int offset = subframe * SilkNsqDelayedDecision.MaxShapingOrder;
            coefficients[offset] = 2000;
            coefficients[offset + 1] = -1000;
            coefficients[offset + 2] = 500;
            coefficients[offset + 3] = -200;
            coefficients[offset + 4] = 100;
            coefficients[offset + 5] = -50;
            coefficients[offset + 6] = 20;
            coefficients[offset + 7] = -10;
            coefficients[offset + 8] = 5;
            coefficients[offset + 9] = -2;
        }

        return coefficients;
    }

    /// <summary>
    /// A minimal greedy quantizer on the same signal chain: one state, the local
    /// rate/distortion minimum at each sample, no look-ahead.
    /// </summary>
    private static (sbyte[] Pulses, int CostQ10) GreedyQuantize(
        int[] xQ3, short[] prediction, short[] shaping, int[] gains, int[] pitchLags)
    {
        int[] lpc = new int[SilkNsqDelayedDecision.LpcBufferLength + SubframeLength];
        int[] ar2 = new int[ShapingOrder];
        int[] shapeRing = new int[SilkNsqDelayedDecision.DecisionDelay];
        int[] pulses = new int[FrameLength];
        int lfAr = 0;
        int sampleBufferIndex = 0;
        int currentSeed = 0;
        int cost = 0;
        int inverseGainQ31 = SilkFixedPoint.Inverse32VarQ(Math.Max(gains[0], 1), 47);
        int inverseGainQ23 = SilkFixedPoint.RshiftRound(inverseGainQ31, 8);

        for (int subframe = 0; subframe < SubframeCount; subframe++)
        {
            for (int i = 0; i < SubframeLength; i++)
            {
                currentSeed = SilkFixedPoint.Rand(currentSeed);
                int lpcBase = SilkNsqDelayedDecision.LpcBufferLength - 1 + i;
                int lpcPrediction = PredictOrder >> 1;
                for (int j = 0; j < PredictOrder; j++)
                {
                    lpcPrediction = SilkFixedPoint.Smlawb(lpcPrediction, lpc[lpcBase - j], prediction[16 + j]);
                }

                lpcPrediction = SilkFixedPoint.Lshift(lpcPrediction, 4);

                int tmp2 = lpc[lpcBase];
                int tmp1 = ar2[0];
                ar2[0] = tmp2;
                int noiseAr = ShapingOrder >> 1;
                noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, shaping[0]);
                for (int j = 2; j < ShapingOrder; j += 2)
                {
                    tmp2 = ar2[j - 1];
                    ar2[j - 1] = tmp1;
                    noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, shaping[j - 1]);
                    tmp1 = ar2[j];
                    ar2[j] = tmp2;
                    noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, shaping[j]);
                }

                ar2[ShapingOrder - 1] = tmp1;
                noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, shaping[ShapingOrder - 1]);
                noiseAr = SilkFixedPoint.Lshift(noiseAr, 1);
                noiseAr = SilkFixedPoint.Smlawb(noiseAr, lfAr, 0);
                noiseAr = SilkFixedPoint.Lshift(noiseAr, 2);

                int noiseLf = SilkFixedPoint.Smulwb(shapeRing[sampleBufferIndex], 2048);
                noiseLf = Smlawt(noiseLf, lfAr, 2048);
                noiseLf = SilkFixedPoint.Lshift(noiseLf, 2);

                int combined = SilkFixedPoint.RshiftRound(
                    SilkFixedPoint.Sub(lpcPrediction, SilkFixedPoint.Add(noiseAr, noiseLf)), 4);
                int xSc = SilkFixedPoint.Smulww(xQ3[(subframe * SubframeLength) + i], inverseGainQ23);
                int rQ10 = SilkFixedPoint.Sub(xSc, combined);
                if (currentSeed < 0)
                {
                    rQ10 = -rQ10;
                }

                rQ10 = rQ10 > 30 << 10 ? 30 << 10 : (rQ10 < -(31 << 10) ? -(31 << 10) : rQ10);
                int q1;
                int q2;
                int rd1;
                int rd2;
                int q1Q0 = (rQ10 - OffsetQ10) >> 10;
                if (q1Q0 > 0)
                {
                    q1 = ((q1Q0 << 10) - 80) + OffsetQ10;
                    q2 = q1 + 1024;
                    rd1 = (short)q1 * 1024;
                    rd2 = (short)q2 * 1024;
                }
                else if (q1Q0 == 0)
                {
                    q1 = OffsetQ10;
                    q2 = q1 + 944;
                    rd1 = (short)q1 * 1024;
                    rd2 = (short)q2 * 1024;
                }
                else if (q1Q0 == -1)
                {
                    q2 = OffsetQ10;
                    q1 = q2 - 944;
                    rd1 = (short)-q1 * 1024;
                    rd2 = (short)q2 * 1024;
                }
                else
                {
                    q1 = ((q1Q0 << 10) + 80) + OffsetQ10;
                    q2 = q1 + 1024;
                    rd1 = (short)-q1 * 1024;
                    rd2 = (short)-q2 * 1024;
                }

                int rr1 = SilkFixedPoint.Sub(rQ10, q1);
                int squared1 = (short)rr1 * (short)rr1;
                int cost1 = (rd1 + squared1) >> 10;
                int rr2 = SilkFixedPoint.Sub(rQ10, q2);
                int squared2 = (short)rr2 * (short)rr2;
                int cost2 = (rd2 + squared2) >> 10;
                // Ties pick the second candidate, matching the delayed-decision rule.
                int q = cost1 < cost2 ? q1 : q2;
                cost += cost1 < cost2 ? cost1 : cost2;
                pulses[(subframe * SubframeLength) + i] = q;

                int excitationQ14 = SilkFixedPoint.Lshift(q, 4);
                if (currentSeed < 0)
                {
                    excitationQ14 = -excitationQ14;
                }

                int xqQ14 = SilkFixedPoint.Add(excitationQ14, lpcPrediction);
                lfAr = SilkFixedPoint.Sub(xqQ14, noiseAr);
                int shape = SilkFixedPoint.Sub(lfAr, noiseLf);
                lpc[SilkNsqDelayedDecision.LpcBufferLength + i] = xqQ14;
                sampleBufferIndex = (sampleBufferIndex - 1) & (SilkNsqDelayedDecision.DecisionDelay - 1);
                shapeRing[sampleBufferIndex] = shape;
                currentSeed = SilkFixedPoint.Add(currentSeed, SilkFixedPoint.RshiftRound(q, 10));
            }

            for (int i = 0; i < SilkNsqDelayedDecision.LpcBufferLength; i++)
            {
                lpc[i] = lpc[i + SubframeLength];
            }
        }

        sbyte[] result = new sbyte[FrameLength];
        for (int i = 0; i < FrameLength; i++)
        {
            result[i] = (sbyte)SilkFixedPoint.RshiftRound(pulses[i], 10);
        }

        return (result, cost);
    }

    /// <summary>
    /// Recomputes the noise-shaping weighted squared error of a pulse sequence on the same
    /// signal chain (distortion term only, no rate term).
    /// </summary>
    private static int WeightedError(
        sbyte[] pulses, int startSeed, int[] xQ3, short[] prediction, short[] shaping, int[] gains)
    {
        int[] lpc = new int[SilkNsqDelayedDecision.LpcBufferLength + SubframeLength];
        int[] ar2 = new int[ShapingOrder];
        int[] shapeRing = new int[SilkNsqDelayedDecision.DecisionDelay];
        int lfAr = 0;
        int sampleBufferIndex = 0;
        int currentSeed = startSeed;
        int error = 0;
        int inverseGainQ23 = SilkFixedPoint.RshiftRound(
            SilkFixedPoint.Inverse32VarQ(Math.Max(gains[0], 1), 47), 8);
        for (int subframe = 0; subframe < SubframeCount; subframe++)
        {
            for (int i = 0; i < SubframeLength; i++)
            {
                currentSeed = SilkFixedPoint.Rand(currentSeed);
                int lpcBase = SilkNsqDelayedDecision.LpcBufferLength - 1 + i;
                int lpcPrediction = PredictOrder >> 1;
                for (int j = 0; j < PredictOrder; j++)
                {
                    lpcPrediction = SilkFixedPoint.Smlawb(lpcPrediction, lpc[lpcBase - j], prediction[16 + j]);
                }

                lpcPrediction = SilkFixedPoint.Lshift(lpcPrediction, 4);
                int tmp2 = lpc[lpcBase];
                int tmp1 = ar2[0];
                ar2[0] = tmp2;
                int noiseAr = ShapingOrder >> 1;
                noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, shaping[0]);
                for (int j = 2; j < ShapingOrder; j += 2)
                {
                    tmp2 = ar2[j - 1];
                    ar2[j - 1] = tmp1;
                    noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, shaping[j - 1]);
                    tmp1 = ar2[j];
                    ar2[j] = tmp2;
                    noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp2, shaping[j]);
                }

                ar2[ShapingOrder - 1] = tmp1;
                noiseAr = SilkFixedPoint.Smlawb(noiseAr, tmp1, shaping[ShapingOrder - 1]);
                noiseAr = SilkFixedPoint.Lshift(noiseAr, 1);
                noiseAr = SilkFixedPoint.Lshift(noiseAr, 2);
                int noiseLf = SilkFixedPoint.Smulwb(shapeRing[sampleBufferIndex], 2048);
                noiseLf = Smlawt(noiseLf, lfAr, 2048);
                noiseLf = SilkFixedPoint.Lshift(noiseLf, 2);

                int combined = SilkFixedPoint.RshiftRound(
                    SilkFixedPoint.Sub(lpcPrediction, SilkFixedPoint.Add(noiseAr, noiseLf)), 4);
                int xSc = SilkFixedPoint.Smulww(xQ3[(subframe * SubframeLength) + i], inverseGainQ23);
                int rQ10 = SilkFixedPoint.Sub(xSc, combined);
                if (currentSeed < 0)
                {
                    rQ10 = -rQ10;
                }

                rQ10 = rQ10 > 30 << 10 ? 30 << 10 : (rQ10 < -(31 << 10) ? -(31 << 10) : rQ10);

                // The decoder rebuilds the quantization level as pulse*1024, minus the level
                // adjustment for positive pulses and plus it for negative pulses, plus the
                // signal-type offset (RFC 6716 §4.2.7.8.6).
                int pulse = pulses[(subframe * SubframeLength) + i];
                int q = pulse > 0 ? (pulse << 10) - 80 + OffsetQ10
                    : pulse < 0 ? (pulse << 10) + 80 + OffsetQ10
                    : OffsetQ10;
                int rr = SilkFixedPoint.Sub(rQ10, q);
                int squared = (short)rr * (short)rr;
                error += squared >> 10;
                int excitationQ14 = SilkFixedPoint.Lshift(q, 4);
                if (currentSeed < 0)
                {
                    excitationQ14 = -excitationQ14;
                }

                int xqQ14 = SilkFixedPoint.Add(excitationQ14, lpcPrediction);
                lfAr = SilkFixedPoint.Sub(xqQ14, noiseAr);
                int shape = SilkFixedPoint.Sub(lfAr, noiseLf);
                lpc[SilkNsqDelayedDecision.LpcBufferLength + i] = xqQ14;
                sampleBufferIndex = (sampleBufferIndex - 1) & (SilkNsqDelayedDecision.DecisionDelay - 1);
                shapeRing[sampleBufferIndex] = shape;
                currentSeed = SilkFixedPoint.Add(currentSeed, SilkFixedPoint.RshiftRound(q, 10));
            }

            for (int i = 0; i < SilkNsqDelayedDecision.LpcBufferLength; i++)
            {
                lpc[i] = lpc[i + SubframeLength];
            }
        }

        return error;
    }

    private static int Smlawt(int a, int b, int c) =>
        unchecked(a + (int)(((long)b * (c >> 16)) >> 16));
}
