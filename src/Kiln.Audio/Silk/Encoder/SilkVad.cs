// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The SILK voice activity detector (RFC 6716 §5.2.3.1; Appendix A, <c>silk/VAD.c</c>).
/// </summary>
/// <remarks>
/// The detector splits the frame into four bands with a two-band allpass filter bank, estimates
/// a per-band noise floor, and turns the band signal-to-noise ratios into a speech activity in
/// Q8 plus per-band quality and a spectral tilt. The analysis is shared with the state in
/// <see cref="SilkVadState"/> and allocates nothing.
/// </remarks>
internal static class SilkVad
{
    private const int NoiseLevelSmoothCoefficientQ16 = 1024;
    private const int NoiseLevelsBias = 50;
    private const int NegativeOffsetQ5 = 128;
    private const int SnrFactorQ16 = 45000;
    private const int SnrSmoothCoefficientQ18 = 4096;
    private const int InternalSubframesLog2 = 2;
    private const int InternalSubframes = 1 << InternalSubframesLog2;

    private const short Allpass20 = 5394 << 1;
    private const short Allpass21 = -24290;

    private static readonly int[] TiltWeights = [30000, 6000, -12000, -12000];

    /// <summary>Filters and decimates a stereo frame with the two-band allpass filter bank.</summary>
    /// <param name="input">Input signal.</param>
    /// <param name="state">Two-word filter state.</param>
    /// <param name="low">Low-band output; must hold at least <c>input.Length / 2</c> samples.</param>
    /// <param name="high">High-band output; must hold at least <c>input.Length / 2</c> samples.</param>
    /// <param name="length">Number of input samples, must be even.</param>
    internal static void AnaFiltBank1(
        ReadOnlySpan<short> input, Span<int> state, Span<short> low, Span<short> high, int length)
    {
        int half = length >> 1;
        for (int k = 0; k < half; k++)
        {
            int input32 = input[2 * k] << 10;
            int y = input32 - state[0];
            int x = SilkFixedPoint.Smlawb(y, y, Allpass21);
            int out1 = state[0] + x;
            state[0] = input32 + x;

            input32 = input[(2 * k) + 1] << 10;
            y = input32 - state[1];
            x = SilkFixedPoint.Smulwb(y, Allpass20);
            int out2 = state[1] + x;
            state[1] = input32 + x;

            low[k] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(out2 + out1, 11));
            high[k] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(out2 - out1, 11));
        }
    }

    /// <summary>Initializes the detector's noise floor estimate.</summary>
    internal static void Initialize(SilkVadState state)
    {
        state.Reset();
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            state.NoiseLevelBias[b] = Math.Max(NoiseLevelsBias / (b + 1), 1);
        }

        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            state.Nl[b] = 100 * state.NoiseLevelBias[b];
            state.InvNl[b] = SilkEncoderMath.Int32Max / state.Nl[b];
        }

        state.Counter = 15;
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            state.NrgRatioSmthQ8[b] = 100 * 256;
        }
    }

    /// <summary>Computes the speech activity and writes the VAD-derived encoder fields.</summary>
    /// <param name="encoder">Encoder state; speech activity, tilt and band qualities are written.</param>
    /// <param name="input">Frame samples, <see cref="SilkEncoderState.FrameLength"/> long.</param>
    internal static void GetSpeechActivity(SilkEncoderState encoder, ReadOnlySpan<short> input)
    {
        SilkVadState state = encoder.Vad;
        int frameLength = encoder.FrameLength;

        int halfLength = SilkEncoderMath.MaxFrameLength / 2;
        Span<short> band0 = stackalloc short[halfLength];
        Span<short> band1 = stackalloc short[halfLength];
        Span<short> band2 = stackalloc short[halfLength];
        Span<short> band3 = stackalloc short[halfLength];

        AnaFiltBank1(input, state.AnaState, band0, band3, frameLength);
        AnaFiltBank1(band0[..(frameLength / 2)], state.AnaState1, band0, band2, frameLength / 2);
        AnaFiltBank1(band0[..(frameLength / 4)], state.AnaState2, band0, band1, frameLength / 4);

        int decimatedFrameLength = frameLength >> 3;
        band0[decimatedFrameLength - 1] >>= 1;
        short hpStateTemp = band0[decimatedFrameLength - 1];
        for (int i = decimatedFrameLength - 1; i > 0; i--)
        {
            band0[i - 1] >>= 1;
            band0[i] -= band0[i - 1];
        }

        band0[0] -= (short)state.HpState;
        state.HpState = hpStateTemp;

        Span<int> energies = stackalloc int[SilkEncoderMath.VadBandCount];
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            int length = frameLength >> Math.Min(SilkEncoderMath.VadBandCount - b, SilkEncoderMath.VadBandCount - 1);
            int subframeLength = length >> InternalSubframesLog2;
            int subframeOffset = 0;
            energies[b] = state.XnrgSubfr[b];
            int sumSquared = 0;
            ReadOnlySpan<short> band = b switch
            {
                0 => band0,
                1 => band1,
                2 => band2,
                _ => band3,
            };
            for (int s = 0; s < InternalSubframes; s++)
            {
                sumSquared = 0;
                for (int i = 0; i < subframeLength; i++)
                {
                    int sample = band[i + subframeOffset] >> 3;
                    sumSquared = unchecked(sumSquared + (sample * sample));
                }

                energies[b] = s < InternalSubframes - 1
                    ? AddPositiveSaturated(energies[b], sumSquared)
                    : AddPositiveSaturated(energies[b], sumSquared >> 1);
                subframeOffset += subframeLength;
            }

            state.XnrgSubfr[b] = sumSquared;
        }

        UpdateNoiseLevels(energies, state);

        int sumSquares = 0;
        int inputTilt = 0;
        Span<int> ratioQ8 = stackalloc int[SilkEncoderMath.VadBandCount];
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            int speechEnergy = energies[b] - state.Nl[b];
            if (speechEnergy > 0)
            {
                if ((energies[b] & unchecked((int)0xFF800000)) == 0)
                {
                    ratioQ8[b] = (energies[b] << 8) / (state.Nl[b] + 1);
                }
                else
                {
                    ratioQ8[b] = energies[b] / ((state.Nl[b] >> 8) + 1);
                }

                int snrQ7 = SilkEncoderMath.Lin2Log(ratioQ8[b]) - (8 * 128);
                sumSquares = unchecked(sumSquares + (snrQ7 * snrQ7));

                if (speechEnergy < 1 << 20)
                {
                    snrQ7 = SilkFixedPoint.Smulwb(SilkFixedPoint.SqrtApprox(speechEnergy) << 6, snrQ7);
                }

                inputTilt = SilkFixedPoint.Smlawb(inputTilt, TiltWeights[b], snrQ7);
            }
            else
            {
                ratioQ8[b] = 256;
            }
        }

        sumSquares /= SilkEncoderMath.VadBandCount;
        int snrDbQ7 = 3 * SilkFixedPoint.SqrtApprox(sumSquares);
        int activityQ15 = SilkEncoderMath.SigmQ15(
            SilkFixedPoint.Smulwb(SnrFactorQ16, snrDbQ7) - NegativeOffsetQ5);

        encoder.InputTiltQ15 = SilkEncoderMath.Shl(SilkEncoderMath.SigmQ15(inputTilt) - 16384, 1);

        int weightedEnergy = 0;
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            weightedEnergy += (b + 1) * ((energies[b] - state.Nl[b]) >> 4);
        }

        if (weightedEnergy <= 0)
        {
            activityQ15 >>= 1;
        }
        else if (weightedEnergy < 32768)
        {
            weightedEnergy = frameLength == 10 * encoder.FsKhz
                ? SilkFixedPoint.LshiftSat32(weightedEnergy, 16)
                : SilkFixedPoint.LshiftSat32(weightedEnergy, 15);
            weightedEnergy = SilkFixedPoint.SqrtApprox(weightedEnergy);
            activityQ15 = SilkFixedPoint.Smulwb(32768 + weightedEnergy, activityQ15);
        }

        encoder.SpeechActivityQ8 = Math.Min(activityQ15 >> 7, 255);

        int smoothCoefficientQ16 = SilkFixedPoint.Smulwb(SnrSmoothCoefficientQ18, SilkFixedPoint.Smulwb(activityQ15, activityQ15));
        if (frameLength == 10 * encoder.FsKhz)
        {
            smoothCoefficientQ16 >>= 1;
        }

        Span<int> bandQuality = encoder.InputQualityBandsQ15;
        for (int b = 0; b < SilkEncoderMath.VadBandCount; b++)
        {
            state.NrgRatioSmthQ8[b] = SilkFixedPoint.Smlawb(
                state.NrgRatioSmthQ8[b], ratioQ8[b] - state.NrgRatioSmthQ8[b], smoothCoefficientQ16);
            int snrQ7 = 3 * (SilkEncoderMath.Lin2Log(state.NrgRatioSmthQ8[b]) - (8 * 128));
            bandQuality[b] = SilkEncoderMath.SigmQ15((snrQ7 - (16 * 128)) >> 4);
        }
    }

    private static void UpdateNoiseLevels(ReadOnlySpan<int> bandEnergies, SilkVadState state)
    {
        int minimumCoefficient = state.Counter < 1000
            ? SilkEncoderMath.Int16Max / ((state.Counter >> 4) + 1)
            : 0;

        for (int k = 0; k < SilkEncoderMath.VadBandCount; k++)
        {
            int noiseLevel = state.Nl[k];
            int energy = AddPositiveSaturated(bandEnergies[k], state.NoiseLevelBias[k]);
            int inverseEnergy = SilkEncoderMath.Int32Max / energy;

            int coefficient;
            if (energy > (noiseLevel << 3))
            {
                coefficient = NoiseLevelSmoothCoefficientQ16 >> 3;
            }
            else if (energy < noiseLevel)
            {
                coefficient = NoiseLevelSmoothCoefficientQ16;
            }
            else
            {
                coefficient = SilkFixedPoint.Smulwb(
                    SilkFixedPoint.Smulww(inverseEnergy, noiseLevel), NoiseLevelSmoothCoefficientQ16 << 1);
            }

            coefficient = Math.Max(coefficient, minimumCoefficient);
            state.InvNl[k] = SilkFixedPoint.Smlawb(state.InvNl[k], inverseEnergy - state.InvNl[k], coefficient);
            noiseLevel = state.InvNl[k] != 0 ? SilkEncoderMath.Int32Max / state.InvNl[k] : SilkEncoderMath.Int32Max;
            state.Nl[k] = Math.Min(noiseLevel, 0x00FFFFFF);
        }

        state.Counter++;
    }

    private static int AddPositiveSaturated(int a, int b) =>
        (int)Math.Min((long)a + b, SilkEncoderMath.Int32Max);
}
