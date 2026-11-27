using Kiln.Audio.Silk;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Tests;

/// <summary>
/// ENC-NSQ-1: the lane-parallel delayed-decision quantizer (one SIMD lane per trellis state) is bit-identical to the
/// original per-state scalar implementation (kept in the tests as <see cref="SilkNsqDelayedDecisionReference"/>) on every
/// kernel tier, over random frames that exercise every state count, signal type, interpolation mode, gain change,
/// warping and the wrap-around of unstable filters, with the state carried across consecutive frames.
/// </summary>
public class SilkNsqLaneParityTests
{
    public static TheoryData<int, int> Cases
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (AudioKernelTier tier in new[]
                     {
                         AudioKernelTier.Scalar, AudioKernelTier.Vector128, AudioKernelTier.AdvSimdArm64, AudioKernelTier.Avx2Fma,
                     })
            {
                for (int seed = 1; seed <= 12; seed++)
                {
                    data.Add((int)tier, seed);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_lane_parallel_quantizer_matches_the_scalar_reference(int tierValue, int seed)
    {
        var tier = (AudioKernelTier)tierValue;
        if (!AudioKernels.IsSupported(tier))
        {
            return;
        }

        var random = new Random(seed * 7919);
        int fsKhz = new[] { 8, 12, 16 }[random.Next(3)];
        int subframeLength = 5 * fsKhz;
        int subframeCount = random.Next(4) == 0 ? 2 : 4;
        int frameLength = subframeLength * subframeCount;
        int ltpMemory = 20 * fsKhz;
        int predictOrder = fsKhz == 16 ? 16 : 10;
        int shapingOrder = new[] { 10, 12, 14, 16 }[random.Next(4)];

        using (AudioKernels.Scope(tier))
        {
            var actual = new SilkNsqDelayedDecision();
            var expected = new SilkNsqDelayedDecisionReference();
            var actualPulses = new sbyte[frameLength];
            var expectedPulses = new sbyte[frameLength];
            var actualXq = new short[frameLength];
            var expectedXq = new short[frameLength];

            for (int frame = 0; frame < 8; frame++)
            {
                int stateCount = 1 + random.Next(4);
                int signalType = random.Next(3);
                int[] xQ3 = new int[frameLength];
                int amplitude = new[] { 300, 2000, 20000 }[random.Next(3)];
                for (int i = 0; i < frameLength; i++)
                {
                    xQ3[i] = random.Next(-amplitude, amplitude + 1);
                }

                short[] prediction = new short[2 * 16];
                for (int row = 0; row < 2; row++)
                {
                    for (int j = 0; j < predictOrder; j++)
                    {
                        int scale = random.Next(4) == 0 ? 6000 : 1500 / (j + 1);
                        prediction[(row * 16) + j] = (short)random.Next(-scale, scale + 1);
                    }
                }

                short[] ltp = new short[5 * subframeCount];
                for (int i = 0; i < ltp.Length; i++)
                {
                    ltp[i] = (short)random.Next(-9000, 9001);
                }

                short[] shaping = new short[16 * subframeCount];
                for (int i = 0; i < shaping.Length; i++)
                {
                    shaping[i] = (short)random.Next(-4000, 4001);
                }

                int[] harmonic = new int[subframeCount];
                int[] tilt = new int[subframeCount];
                int[] lowFrequency = new int[subframeCount];
                int[] gains = new int[subframeCount];
                int[] lags = new int[subframeCount];
                int baseLag = random.Next(2 * fsKhz, ltpMemory - 40);
                for (int k = 0; k < subframeCount; k++)
                {
                    harmonic[k] = random.Next(0, 16384);
                    tilt[k] = random.Next(-8000, 8001);
                    lowFrequency[k] = (random.Next(-8000, 8001) << 16) | (random.Next(-8000, 8001) & 0xFFFF);
                    gains[k] = random.Next(3) == 0 && k > 0 ? gains[k - 1] : random.Next(1 << 15, 1 << 22);
                    lags[k] = signalType == 2 ? Math.Clamp(baseLag + random.Next(-4, 5), 2 * fsKhz, ltpMemory - 40) : 0;
                }

                int warping = random.Next(3) == 0 ? 0 : random.Next(0, 14000);
                int lambda = random.Next(0, 2048);
                int offsetType = random.Next(2);
                int interpolation = random.Next(3) == 0 ? 4 : random.Next(0, 4);
                int quantSeed = random.Next(4);

                SilkNsqParameters Make() => new(
                    xQ3, prediction, ltp, shaping, harmonic, tilt, lowFrequency, gains, lags,
                    signalType, offsetType, lambda, 15565, quantSeed, interpolation,
                    subframeCount, subframeLength, ltpMemory, predictOrder, shapingOrder, warping);

                actual.Run(Make(), stateCount, actualPulses, actualXq, out int actualSeed);
                expected.Run(Make(), stateCount, expectedPulses, expectedXq, out int expectedSeed);

                string where = $"{tier} seed {seed} frame {frame} states {stateCount} type {signalType} fs {fsKhz} sf {subframeCount}";
                Assert.True(expectedPulses.AsSpan().SequenceEqual(actualPulses), $"pulses differ: {where}");
                Assert.True(expectedXq.AsSpan().SequenceEqual(actualXq), $"output differs: {where}");
                Assert.True(expectedSeed == actualSeed, $"seed differs: {where}");
                Assert.True(expected.LastCostQ10 == actual.LastCostQ10, $"cost differs: {where}");
                Assert.True(expected.LagPrev == actual.LagPrev, $"lag differs: {where}");
            }
        }
    }
}
