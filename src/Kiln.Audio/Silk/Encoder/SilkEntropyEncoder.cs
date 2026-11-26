// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// Side-information and excitation entropy coding (RFC 6716 §5.2.3; Appendix A,
/// <c>silk/encode_indices.c</c>, <c>silk/encode_pulses.c</c>, <c>silk/shell_coder.c</c> and
/// <c>silk/code_signs.c</c>).
/// </summary>
/// <remarks>
/// The probability tables are the decoder's forward tables out of 256, so encoding a symbol is
/// the exact inverse of <see cref="SilkTables.DecodePdf"/> and the range-coder state matches on
/// both sides.
/// </remarks>
internal static class SilkEntropyEncoder
{
    private const int ShellBlockSize = 16;
    private const int ShellBlockShift = 4;

    /// <summary>Encodes the side information of one SILK frame.</summary>
    internal static void EncodeIndices(
        ref RangeEncoder rangeEncoder,
        SilkEncoderState encoder,
        int frameIndex,
        int conditionalCoding)
    {
        SilkSideInfo indices = encoder.Indices;
        SilkNlsfCodebook codebook = encoder.PredictLpcOrder == 16 ? SilkEncoderTables.NlsfWb : SilkEncoderTables.NlsfNbMb;
        bool voiced = indices.SignalType == 2;
        int typeOffset = (2 * indices.SignalType) + indices.QuantOffsetType;

        EncodePdf(ref rangeEncoder, encoder.VadFlags[frameIndex] != 0 ? SilkTables.FrameTypeActivePdf : SilkTables.FrameTypeInactivePdf, typeOffset);

        if (conditionalCoding == 2)
        {
            EncodePdf(ref rangeEncoder, SilkTables.GainDeltaPdf, indices.GainsIndices[0]);
        }
        else
        {
            EncodePdf(ref rangeEncoder, SilkTables.GainMsbPdf[indices.SignalType], indices.GainsIndices[0] >> 3);
            EncodePdf(ref rangeEncoder, SilkTables.GainLsbPdf, indices.GainsIndices[0] & 7);
        }

        for (int i = 1; i < encoder.SubframeCount; i++)
        {
            EncodePdf(ref rangeEncoder, SilkTables.GainDeltaPdf, indices.GainsIndices[i]);
        }

        EncodePdf(ref rangeEncoder, codebook.Stage1Pdf(indices.SignalType), indices.NlsfIndices[0]);

        string selection = codebook.Stage2Select[indices.NlsfIndices[0]];
        int letterBase = codebook.Order == 16 ? 'i' : 'a';
        for (int i = 0; i < codebook.Order; i++)
        {
            int residual = indices.NlsfIndices[i + 1];
            ReadOnlySpan<byte> pdf = codebook.Order == 16
                ? SilkTables.NlsfStage2WbPdf[selection[i] - letterBase]
                : SilkTables.NlsfStage2NbMbPdf[selection[i] - letterBase];

            if (residual >= 4)
            {
                EncodePdf(ref rangeEncoder, pdf, 8);
                EncodePdf(ref rangeEncoder, SilkTables.NlsfExtensionPdf, residual - 4);
            }
            else if (residual <= -4)
            {
                EncodePdf(ref rangeEncoder, pdf, 0);
                EncodePdf(ref rangeEncoder, SilkTables.NlsfExtensionPdf, -residual - 4);
            }
            else
            {
                EncodePdf(ref rangeEncoder, pdf, residual + 4);
            }
        }

        if (encoder.SubframeCount == 4)
        {
            EncodePdf(ref rangeEncoder, SilkTables.NlsfInterpolationPdf, indices.NlsfInterpCoefQ2);
        }

        if (voiced)
        {
            EncodePitchLag(ref rangeEncoder, encoder, indices, conditionalCoding);
            EncodePdf(ref rangeEncoder, ContourPdf(encoder), indices.ContourIndex);
            EncodePdf(ref rangeEncoder, SilkTables.LtpPeriodicityPdf, indices.PerIndex);
            for (int k = 0; k < encoder.SubframeCount; k++)
            {
                EncodePdf(ref rangeEncoder, SilkTables.LtpFilterPdf[indices.PerIndex], indices.LtpIndex[k]);
            }

            if (conditionalCoding == 0)
            {
                EncodePdf(ref rangeEncoder, SilkTables.LtpScalingPdf, indices.LtpScaleIndex);
            }
        }

        encoder.EcPrevSignalType = indices.SignalType;
        EncodePdf(ref rangeEncoder, SilkTables.SeedPdf, indices.Seed);
    }

    /// <summary>Encodes the excitation pulses (shell coding, LSBs and signs).</summary>
    internal static void EncodePulses(
        ref RangeEncoder rangeEncoder,
        int signalType,
        int quantOffsetType,
        Span<short> pulses,
        int frameLength)
    {
        Span<int> absolute = stackalloc int[SilkEncoderMath.MaxFrameLength];
        Span<int> sums = stackalloc int[SilkEncoderMath.MaxShellBlocks];
        Span<int> rightShifts = stackalloc int[SilkEncoderMath.MaxShellBlocks];
        Span<int> combined = stackalloc int[8];

        int blocks = frameLength >> ShellBlockShift;
        if (blocks * ShellBlockSize < frameLength)
        {
            blocks++;
            pulses.Slice(frameLength, ShellBlockSize).Clear();
        }

        for (int i = 0; i < blocks * ShellBlockSize; i += 4)
        {
            absolute[i] = SilkEncoderMath.Abs(pulses[i]);
            absolute[i + 1] = SilkEncoderMath.Abs(pulses[i + 1]);
            absolute[i + 2] = SilkEncoderMath.Abs(pulses[i + 2]);
            absolute[i + 3] = SilkEncoderMath.Abs(pulses[i + 3]);
        }

        for (int block = 0; block < blocks; block++)
        {
            rightShifts[block] = 0;
            int offset = block * ShellBlockSize;
            while (true)
            {
                int scaleDown = CombineAndCheck(combined, absolute[offset..], SilkEncoderTables.MaxPulsesTable[0], 8);
                scaleDown += CombineAndCheck(combined, combined, SilkEncoderTables.MaxPulsesTable[1], 4);
                scaleDown += CombineAndCheck(combined, combined, SilkEncoderTables.MaxPulsesTable[2], 2);
                scaleDown += CombineAndCheck(combined, combined, SilkEncoderTables.MaxPulsesTable[3], 1);
                sums[block] = combined[0];

                if (scaleDown == 0)
                {
                    break;
                }

                rightShifts[block]++;
                for (int k = 0; k < ShellBlockSize; k++)
                {
                    absolute[offset + k] >>= 1;
                }
            }
        }

        int rateLevel = 0;
        int minimumBitsQ5 = SilkEncoderMath.Int32Max;
        for (int level = 0; level < SilkEncoderTables.RateLevelCount; level++)
        {
            int bitsQ5 = SilkEncoderTables.RateLevelsBitsQ5[signalType >> 1][level];
            ReadOnlySpan<byte> pulseBits = SilkEncoderTables.PulsesPerBlockBitsQ5[level];
            for (int block = 0; block < blocks; block++)
            {
                bitsQ5 += rightShifts[block] > 0 ? pulseBits[17] : pulseBits[sums[block]];
            }

            if (bitsQ5 < minimumBitsQ5)
            {
                minimumBitsQ5 = bitsQ5;
                rateLevel = level;
            }
        }

        EncodePdf(ref rangeEncoder, signalType == 2 ? SilkTables.RateLevelVoicedPdf : SilkTables.RateLevelInactivePdf, rateLevel);

        for (int block = 0; block < blocks; block++)
        {
            if (rightShifts[block] == 0)
            {
                EncodePdf(ref rangeEncoder, SilkTables.PulseCountPdf[rateLevel], sums[block]);
            }
            else
            {
                EncodePdf(ref rangeEncoder, SilkTables.PulseCountPdf[rateLevel], 17);
                for (int k = 0; k < rightShifts[block] - 1; k++)
                {
                    EncodePdf(ref rangeEncoder, SilkTables.PulseCountPdf[9], 17);
                }

                EncodePdf(ref rangeEncoder, SilkTables.PulseCountPdf[9], sums[block]);
            }
        }

        for (int block = 0; block < blocks; block++)
        {
            if (sums[block] > 0)
            {
                EncodeShell(ref rangeEncoder, absolute.Slice(block * ShellBlockSize, ShellBlockSize));
            }
        }

        for (int block = 0; block < blocks; block++)
        {
            if (rightShifts[block] > 0)
            {
                int offset = block * ShellBlockSize;
                int lsbCount = rightShifts[block] - 1;
                for (int k = 0; k < ShellBlockSize; k++)
                {
                    int magnitude = SilkEncoderMath.Abs(pulses[offset + k]);
                    for (int j = lsbCount; j > 0; j--)
                    {
                        EncodePdf(ref rangeEncoder, SilkTables.LsbPdf, (magnitude >> j) & 1);
                    }

                    EncodePdf(ref rangeEncoder, SilkTables.LsbPdf, magnitude & 1);
                }
            }
        }

        int signBlocks = (frameLength + (ShellBlockSize / 2)) >> ShellBlockShift;
        byte[][] signRows = SilkTables.SignPdf[signalType][quantOffsetType];
        for (int block = 0; block < signBlocks; block++)
        {
            int pulseCount = sums[block];
            if (pulseCount > 0)
            {
                ReadOnlySpan<byte> signPdf = signRows[Math.Min(pulseCount & 0x1F, 6)];
                int offset = block * ShellBlockSize;
                for (int k = 0; k < ShellBlockSize; k++)
                {
                    int pulse = pulses[offset + k];
                    if (pulse != 0)
                    {
                        EncodePdf(ref rangeEncoder, signPdf, pulse > 0 ? 1 : 0);
                    }
                }
            }
        }
    }

    /// <summary>Encodes one symbol from a forward probability table out of 256.</summary>
    internal static void EncodePdf(ref RangeEncoder rangeEncoder, ReadOnlySpan<byte> pdf, int symbol)
    {
        uint lower = 0;
        for (int k = 0; k < symbol; k++)
        {
            lower += pdf[k];
        }

        uint upper = lower + pdf[symbol];
        rangeEncoder.Encode(lower, upper, 256);
    }

    private static void EncodePitchLag(
        ref RangeEncoder rangeEncoder,
        SilkEncoderState encoder,
        SilkSideInfo indices,
        int conditionalCoding)
    {
        bool absolute = true;
        if (conditionalCoding == 2 && encoder.EcPrevSignalType == 2)
        {
            int delta = indices.LagIndex - encoder.EcPrevLagIndex;
            if (delta is >= -8 and <= 11)
            {
                EncodePdf(ref rangeEncoder, SilkTables.PitchLagDeltaPdf, delta + 9);
                absolute = false;
            }
            else
            {
                EncodePdf(ref rangeEncoder, SilkTables.PitchLagDeltaPdf, 0);
            }
        }

        if (absolute)
        {
            int scale = encoder.FsKhz >> 1;
            int high = indices.LagIndex / scale;
            int low = indices.LagIndex - (high * scale);
            int bandwidthIndex = encoder.FsKhz == 8 ? 0 : (encoder.FsKhz == 12 ? 1 : 2);
            EncodePdf(ref rangeEncoder, SilkTables.PitchLagHighPdf, high);
            EncodePdf(ref rangeEncoder, SilkTables.PitchLagLowPdf[bandwidthIndex], low);
        }

        encoder.EcPrevLagIndex = indices.LagIndex;
    }

    private static ReadOnlySpan<byte> ContourPdf(SilkEncoderState encoder)
    {
        bool narrowband = encoder.FsKhz == 8;
        return (narrowband, encoder.SubframeCount) switch
        {
            (true, 2) => SilkTables.PitchContourPdfNb10,
            (true, _) => SilkTables.PitchContourPdfNb20,
            (false, 2) => SilkTables.PitchContourPdfMbWb10,
            _ => SilkTables.PitchContourPdfMbWb20,
        };
    }

    private static int CombineAndCheck(Span<int> output, ReadOnlySpan<int> input, int maximumPulses, int length)
    {
        for (int k = 0; k < length; k++)
        {
            int sum = input[2 * k] + input[(2 * k) + 1];
            if (sum > maximumPulses)
            {
                return 1;
            }

            output[k] = sum;
        }

        return 0;
    }

    private static void EncodeShell(ref RangeEncoder rangeEncoder, scoped ReadOnlySpan<int> pulses)
    {
        Span<int> level1 = stackalloc int[8];
        Span<int> level2 = stackalloc int[4];
        Span<int> level3 = stackalloc int[2];
        Span<int> level4 = stackalloc int[1];

        Combine(level1, pulses, 8);
        Combine(level2, level1, 4);
        Combine(level3, level2, 2);
        Combine(level4, level3, 1);

        EncodeSplit(ref rangeEncoder, level3[0], level4[0], 16);
        EncodeSplit(ref rangeEncoder, level2[0], level3[0], 8);
        EncodeSplit(ref rangeEncoder, level1[0], level2[0], 4);
        EncodeSplit(ref rangeEncoder, pulses[0], level1[0], 2);
        EncodeSplit(ref rangeEncoder, pulses[2], level1[1], 2);
        EncodeSplit(ref rangeEncoder, level1[2], level2[1], 4);
        EncodeSplit(ref rangeEncoder, pulses[4], level1[2], 2);
        EncodeSplit(ref rangeEncoder, pulses[6], level1[3], 2);
        EncodeSplit(ref rangeEncoder, level2[2], level3[1], 8);
        EncodeSplit(ref rangeEncoder, level1[4], level2[2], 4);
        EncodeSplit(ref rangeEncoder, pulses[8], level1[4], 2);
        EncodeSplit(ref rangeEncoder, pulses[10], level1[5], 2);
        EncodeSplit(ref rangeEncoder, level1[6], level2[3], 4);
        EncodeSplit(ref rangeEncoder, pulses[12], level1[6], 2);
        EncodeSplit(ref rangeEncoder, pulses[14], level1[7], 2);
    }

    private static void Combine(Span<int> output, ReadOnlySpan<int> input, int length)
    {
        for (int k = 0; k < length; k++)
        {
            output[k] = input[2 * k] + input[(2 * k) + 1];
        }
    }

    private static void EncodeSplit(ref RangeEncoder rangeEncoder, int firstChild, int total, int partitionSize)
    {
        if (total > 0)
        {
            EncodePdf(ref rangeEncoder, SilkTables.PulseSplitPdf(partitionSize, total), firstChild);
        }
    }
}
