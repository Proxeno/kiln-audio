// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>Band energy analysis and quantization for the CELT encoder (RFC 6716 §4.3.2).</summary>
internal static class CeltEncoderEnergy
{
    private const int BandCount = CeltTables.BandCount;
    private const int MinProbability = 1;
    private const int MinValues = 16;

    /// <summary>
    /// Computes the band log energies from the band amplitudes (RFC 6716 Appendix A,
    /// celt/quant_bands.c: <c>amp2Log2</c>).
    /// </summary>
    internal static void Amp2Log2(
        ReadOnlySpan<float> bandEnergy, int channels, int effEnd, int end, Span<float> bandLogE)
    {
        ReadOnlySpan<float> means = CeltTables.EMeans;
        for (int c = 0; c < channels; c++)
        {
            int offset = c * BandCount;
            for (int i = 0; i < effEnd; i++)
            {
                bandLogE[offset + i] =
                    (float)(1.442695040888963387 * Math.Log(bandEnergy[offset + i])) - means[i];
            }

            for (int i = effEnd; i < end; i++)
            {
                bandLogE[offset + i] = -14f;
            }
        }
    }

    /// <summary>
    /// Encodes the coarse energy of every band and channel (RFC 6716 §4.3.2.1; RFC 6716 Appendix A,
    /// celt/quant_bands.c: <c>quant_coarse_energy</c>).
    /// </summary>
    /// <remarks>
    /// Below complexity 4 the inter/intra decision uses the reference's delayed-intra heuristic.
    /// From complexity 4 the reference's two-pass trial is reproduced: the intra pass is run on a
    /// copy of the range-encoder state over the same output buffer, the buffer and state are saved,
    /// the encoder is rewound, the inter pass is run, and the intra result is restored when it is
    /// better. The frozen <c>RangeEncoder</c> type is only copied by value, never extended.
    /// </remarks>
    internal static void QuantCoarseEnergy(
        ref RangeEncoder enc,
        int lm,
        int start,
        int end,
        int effEnd,
        int channels,
        ReadOnlySpan<float> bandLogE,
        Span<float> oldBandE,
        Span<float> error,
        Span<float> oldIntra,
        Span<float> errorIntra,
        Span<byte> frameBuffer,
        Span<byte> snapshot,
        int nbAvailableBytes,
        int budget,
        bool twoPass,
        ref float delayedIntra)
    {
        ReadOnlySpan<byte> model = CeltTables.EnergyProbabilityModel.Slice(lm * 2 * 42, 2 * 42);

        float newDistortion = LossDistortion(bandLogE, oldBandE, start, effEnd, channels);
        bool intra = !twoPass
            && delayedIntra > 2f * channels * (end - start)
            && nbAvailableBytes > (end - start) * channels;

        int tell = enc.Tell();
        if (tell + 3 > budget)
        {
            twoPass = false;
            intra = false;
        }

        float maxDecay = MathF.Min(16f, 0.125f * nbAvailableBytes);
        RangeEncoder startState = enc;
        int badness1 = 0;

        if (twoPass || intra)
        {
            oldBandE.CopyTo(oldIntra);
            badness1 = QuantCoarseEnergyImpl(
                ref enc, lm, start, end, channels, bandLogE, oldIntra, errorIntra, model.Slice(42, 42),
                budget, intra: true, maxDecay);
        }

        if (!intra)
        {
            int tellIntra = (int)enc.TellFrac();
            RangeEncoder intraState = enc;
            if (twoPass)
            {
                frameBuffer.CopyTo(snapshot);
            }

            enc = startState;

            int badness2 = QuantCoarseEnergyImpl(
                ref enc, lm, start, end, channels, bandLogE, oldBandE, error, model.Slice(0, 42),
                budget, intra: false, maxDecay);

            if (twoPass && (badness1 < badness2 || (badness1 == badness2 && (int)enc.TellFrac() > tellIntra)))
            {
                enc = intraState;
                snapshot.Slice(0, frameBuffer.Length).CopyTo(frameBuffer);
                oldIntra.CopyTo(oldBandE);
                errorIntra.CopyTo(error);
                intra = true;
            }
        }
        else
        {
            oldIntra.CopyTo(oldBandE);
            errorIntra.CopyTo(error);
        }

        delayedIntra = intra
            ? newDistortion
            : (PredictionCoefficient(lm) * PredictionCoefficient(lm) * delayedIntra) + newDistortion;
    }

    private static float PredictionCoefficient(int lm) => CeltTables.PredictionCoefficient[lm] / 32768f;

    private static int QuantCoarseEnergyImpl(
        ref RangeEncoder enc,
        int lm,
        int start,
        int end,
        int channels,
        ReadOnlySpan<float> bandLogE,
        Span<float> oldBandE,
        Span<float> error,
        ReadOnlySpan<byte> model,
        int budget,
        bool intra,
        float maxDecay)
    {
        if (enc.Tell() + 3 <= budget)
        {
            enc.EncodeBitLogp(intra ? 1 : 0, 3);
        }

        float coef = intra ? 0f : PredictionCoefficient(lm);
        float beta = intra ? CeltTables.BetaIntra / 32768f : CeltTables.BetaCoefficient[lm] / 32768f;
        Span<float> previous = stackalloc float[2];
        previous.Clear();
        int badness = 0;

        for (int i = start; i < end; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int index = (c * BandCount) + i;
                float x = bandLogE[index];
                float oldE = MathF.Max(-9f, oldBandE[index]);
                float f = x - (coef * oldE) - previous[c];

                // Rounding to nearest integer here is really important.
                int qi = (int)MathF.Floor(0.5f + f);
                float decayBound = MathF.Max(-28f, oldBandE[index]) - maxDecay;
                if (qi < 0 && x < decayBound)
                {
                    qi += (int)(decayBound - x);
                    if (qi > 0)
                    {
                        qi = 0;
                    }
                }

                int qi0 = qi;
                int tell = enc.Tell();
                int bitsLeft = budget - tell - (3 * channels * (end - i));
                if (i != start && bitsLeft < 30)
                {
                    if (bitsLeft < 24)
                    {
                        qi = Math.Min(1, qi);
                    }

                    if (bitsLeft < 16)
                    {
                        qi = Math.Max(-1, qi);
                    }
                }

                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    LaplaceEncode(ref enc, ref qi, (uint)model[pi] << 7, model[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = Math.Max(-1, Math.Min(qi, 1));
                    int symbol = (2 * qi) ^ (qi < 0 ? -1 : 0);
                    enc.EncodeIcdf(CeltTables.SmallEnergyIcdf, 2, symbol);
                }
                else if (budget - tell >= 1)
                {
                    qi = Math.Min(0, qi);
                    enc.EncodeBitLogp(-qi, 1);
                }
                else
                {
                    qi = -1;
                }

                error[index] = f - qi;
                badness += Math.Abs(qi0 - qi);
                oldBandE[index] = (coef * oldE) + previous[c] + qi;
                previous[c] += qi - (beta * qi);
            }
        }

        return badness;
    }

    /// <summary>Encodes a Laplace-distributed energy residual (RFC 6716 Appendix A, celt/laplace.c).</summary>
    internal static void LaplaceEncode(ref RangeEncoder enc, ref int value, uint fs, int decay)
    {
        uint fl = 0;
        int val = value;
        if (val != 0)
        {
            int s = val < 0 ? -1 : 0;
            val = (val + s) ^ s;
            fl = fs;
            fs = LaplaceFrequency(fs, decay);
            int i = 1;
            for (; fs > 0 && i < val; i++)
            {
                fs *= 2;
                fl += fs + (uint)(2 * MinProbability);
                fs = (uint)((fs * (long)decay) >> 15);
            }

            if (fs == 0)
            {
                int ndiMax = (int)(32768 - fl + MinProbability - 1);
                ndiMax = (ndiMax - s) >> 1;
                int di = Math.Min(val - i, ndiMax - 1);
                fl += (uint)(((2 * di) + 1 + s) * MinProbability);
                fs = (uint)Math.Min(MinProbability, 32768 - fl);
                value = (i + di + s) ^ s;
            }
            else
            {
                fs += MinProbability;
                if (s == 0)
                {
                    fl += fs;
                }
            }
        }

        enc.EncodeBin(fl, fl + fs, 15);
    }

    private static uint LaplaceFrequency(uint fs0, int decay)
    {
        uint total = 32768u - (MinProbability * (2 * MinValues)) - fs0;
        return unchecked((total * (uint)(16384 - decay)) >> 15);
    }

    /// <summary>Returns the delayed-intra distortion estimate (RFC 6716 Appendix A, celt/quant_bands.c: loss_distortion).</summary>
    private static float LossDistortion(
        ReadOnlySpan<float> bandLogE, ReadOnlySpan<float> oldBandE, int start, int end, int channels)
    {
        float dist = 0f;
        for (int c = 0; c < channels; c++)
        {
            int offset = c * BandCount;
            for (int i = start; i < end; i++)
            {
                float d = bandLogE[offset + i] - oldBandE[offset + i];
                dist += d * d;
            }
        }

        return MathF.Min(200f, dist);
    }

    /// <summary>Encodes the fine energy bits (RFC 6716 §4.3.2.2; RFC 6716 Appendix A, quant_bands.c: quant_fine_energy).</summary>
    internal static void QuantFineEnergy(
        ref RangeEncoder enc,
        int start,
        int end,
        int channels,
        Span<float> oldBandE,
        Span<float> error,
        ReadOnlySpan<int> fineQuant)
    {
        for (int i = start; i < end; i++)
        {
            int quant = fineQuant[i];
            if (quant <= 0)
            {
                continue;
            }

            int frac = 1 << quant;
            for (int c = 0; c < channels; c++)
            {
                int index = (c * BandCount) + i;
                int q2 = (int)MathF.Floor((error[index] + 0.5f) * frac);
                q2 = Math.Clamp(q2, 0, frac - 1);
                enc.EncodeBits((uint)q2, quant);
                float offset = (((q2 + 0.5f) * (1 << (14 - quant))) * (1f / 16384f)) - 0.5f;
                oldBandE[index] += offset;
                error[index] -= offset;
            }
        }
    }

    /// <summary>Uses the remaining bits on extra fine energy (RFC 6716 Appendix A, quant_bands.c: quant_energy_finalise).</summary>
    internal static void QuantEnergyFinalise(
        ref RangeEncoder enc,
        int start,
        int end,
        int channels,
        Span<float> oldBandE,
        ReadOnlySpan<float> error,
        ReadOnlySpan<int> fineQuant,
        ReadOnlySpan<int> finePriority,
        int bitsLeft)
    {
        for (int priority = 0; priority < 2; priority++)
        {
            for (int i = start; i < end && bitsLeft >= channels; i++)
            {
                if (fineQuant[i] >= CeltTables.MaxFineBits || finePriority[i] != priority)
                {
                    continue;
                }

                for (int c = 0; c < channels; c++)
                {
                    int index = (c * BandCount) + i;
                    int q2 = error[index] < 0f ? 0 : 1;
                    enc.EncodeBits((uint)q2, 1);
                    float offset = (q2 - 0.5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384f);
                    oldBandE[index] += offset;
                    bitsLeft--;
                }
            }
        }
    }
}