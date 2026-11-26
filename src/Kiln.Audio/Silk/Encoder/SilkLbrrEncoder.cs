// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Silk;

/// <summary>Writes the side information and excitation of one LBRR frame.</summary>
/// <remarks>
/// The LBRR frames of an Opus frame reuse the parameters of the regular frames they describe but
/// are entropy-coded at the lower LBRR rate (RFC 6716 §4.2.4, §4.2.5; Appendix A
/// <c>silk_LBRR_encode_FIX</c>). The writer is supplied by the SILK core encoder, which owns the
/// parameter and pulse coding.
/// </remarks>
internal interface ISilkLbrrFrameWriter
{
    /// <summary>Writes one LBRR frame's side information followed by its excitation.</summary>
    /// <param name="enc">The range encoder for this Opus frame.</param>
    /// <param name="frameIndex">The 20 ms interval index within the packet.</param>
    /// <param name="channel">0 for mid (or mono), 1 for side.</param>
    /// <param name="conditionalCoding">True to delta-code against the previous LBRR frame.</param>
    void WriteLbrrFrame(ref RangeEncoder enc, int frameIndex, int channel, bool conditionalCoding);
}

/// <summary>
/// Selection and layout of the low-bitrate redundancy (LBRR) frames that carry in-band FEC
/// (RFC 6716 §4.2.4, §4.2.5; Appendix A <c>silk_LBRR_encode_FIX</c> and <c>silk_Encode</c>).
/// </summary>
/// <remarks>
/// <para>
/// This module owns the parts of LBRR coding that are not the SILK core encoder's: whether LBRR
/// is enabled for the current loss target and bitrate, which frames are redundantly coded, the
/// gain increase that lowers the redundancy rate, and the exact flag and frame layout the decoder
/// reads. The frame payloads themselves are delegated to <see cref="ISilkLbrrFrameWriter"/>.
/// </para>
/// <para>
/// The flags are written with the same forward PDFs the decoder uses
/// (<see cref="SilkTables.LbrrFlags40Pdf"/> and <see cref="SilkTables.LbrrFlags60Pdf"/>), in the
/// same order as RFC 6716 §4.2.3 and §4.2.4, so the bytes round-trip through
/// <see cref="SilkDecoder"/> unchanged.
/// </para>
/// </remarks>
internal static class SilkLbrrEncoder
{
    /// <summary>The smallest bitrate that enables LBRR at 8 kHz (Appendix A <c>LBRR_NB_MIN_RATE_BPS</c>).</summary>
    internal const int MinimumRateNbBps = 12000;

    /// <summary>The smallest bitrate that enables LBRR at 12 kHz (Appendix A <c>LBRR_MB_MIN_RATE_BPS</c>).</summary>
    internal const int MinimumRateMbBps = 14000;

    /// <summary>The smallest bitrate that enables LBRR at 16 kHz (Appendix A <c>LBRR_WB_MIN_RATE_BPS</c>).</summary>
    internal const int MinimumRateWbBps = 16000;

    /// <summary>The speech activity above which a frame is redundantly coded, Q8 (0.3).</summary>
    internal const int SpeechActivityThresholdQ8 = 77;

    private const int RateScaleQ16 = 655;        // SILK_FIX_CONST(0.01, 16)
    private const int LossScaleQ16 = 26214;      // SILK_FIX_CONST(0.4, 16)
    private const int MaxQuantizationGainLevel = 64; // N_LEVELS_QGAIN

    /// <summary>
    /// Decides whether in-band FEC is usable and how much to raise the LBRR gain
    /// (Appendix A <c>silk_setup_LBRR</c>).
    /// </summary>
    /// <param name="useInBandFec">The caller's FEC request.</param>
    /// <param name="packetLossPercent">The expected loss, 0 to 100.</param>
    /// <param name="fsKHz">The internal SILK rate in kHz.</param>
    /// <param name="targetRateBps">The target bitrate.</param>
    /// <param name="enabled">Receives whether LBRR may be coded.</param>
    /// <param name="gainIncreases">Receives the gain increase applied to LBRR frames, or 0.</param>
    public static void Setup(
        bool useInBandFec,
        int packetLossPercent,
        int fsKHz,
        int targetRateBps,
        out bool enabled,
        out int gainIncreases)
    {
        enabled = false;
        gainIncreases = 0;
        if (!useInBandFec || packetLossPercent <= 0)
        {
            return;
        }

        int minimumRate = fsKHz switch
        {
            8 => MinimumRateNbBps,
            12 => MinimumRateMbBps,
            16 => MinimumRateWbBps,
            _ => throw new ArgumentOutOfRangeException(nameof(fsKHz), fsKHz, "The internal rate must be 8, 12 or 16 kHz."),
        };

        int scaledRate = SilkFixedPoint.Smulwb(
            SilkFixedPoint.Mul(minimumRate, 125 - Math.Min(packetLossPercent, 25)), RateScaleQ16);
        if (targetRateBps > scaledRate)
        {
            enabled = true;
            gainIncreases = Math.Max(7 - SilkFixedPoint.Smulwb(packetLossPercent, LossScaleQ16), 2);
        }
    }

    /// <summary>Returns whether a frame with this speech activity is coded redundantly.</summary>
    /// <param name="enabled">Whether LBRR is enabled by <see cref="Setup"/>.</param>
    /// <param name="speechActivityQ8">The frame's speech activity, Q8.</param>
    public static bool ShouldCodeFrame(bool enabled, int speechActivityQ8) =>
        enabled && speechActivityQ8 > SpeechActivityThresholdQ8;

    /// <summary>
    /// Returns the first-subframe gain index of an LBRR frame (Appendix A
    /// <c>silk_LBRR_encode_FIX</c>).
    /// </summary>
    /// <param name="regularGainIndex">The regular frame's first-subframe gain index.</param>
    /// <param name="gainIncreases">The increase from <see cref="Setup"/>.</param>
    public static int RaiseFirstGainIndex(int regularGainIndex, int gainIncreases) =>
        Math.Min(regularGainIndex + gainIncreases, MaxQuantizationGainLevel - 1);

    /// <summary>
    /// Writes the VAD bits, the per-channel LBRR flags and, for 40 and 60 ms frames, the
    /// per-frame LBRR masks (RFC 6716 §4.2.3, §4.2.4).
    /// </summary>
    /// <param name="enc">The range encoder for this Opus frame.</param>
    /// <param name="channels">The SILK layer channel count, 1 or 2.</param>
    /// <param name="silkFrames">The number of 20 ms intervals, 1 to 3.</param>
    /// <param name="midVadMask">The mid channel's VAD bits, interval 0 in the LSB.</param>
    /// <param name="sideVadMask">The side channel's VAD bits, interval 0 in the LSB.</param>
    /// <param name="midLbrrMask">The mid channel's LBRR bits, interval 0 in the LSB.</param>
    /// <param name="sideLbrrMask">The side channel's LBRR bits, interval 0 in the LSB.</param>
    public static void EncodeHeader(
        ref RangeEncoder enc,
        int channels,
        int silkFrames,
        int midVadMask,
        int sideVadMask,
        int midLbrrMask,
        int sideLbrrMask)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "The SILK layer has one or two channels.");
        }

        if (silkFrames is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(silkFrames), silkFrames, "One to three 20 ms intervals are supported.");
        }

        for (int f = 0; f < silkFrames; f++)
        {
            enc.EncodeBitLogp((midVadMask >> f) & 1, 1);
        }

        enc.EncodeBitLogp(midLbrrMask != 0 ? 1 : 0, 1);

        if (channels == 2)
        {
            for (int f = 0; f < silkFrames; f++)
            {
                enc.EncodeBitLogp((sideVadMask >> f) & 1, 1);
            }

            enc.EncodeBitLogp(sideLbrrMask != 0 ? 1 : 0, 1);
        }

        if (midLbrrMask != 0 && silkFrames > 1)
        {
            EncodePdf(ref enc, silkFrames == 2 ? SilkTables.LbrrFlags40Pdf : SilkTables.LbrrFlags60Pdf, midLbrrMask);
        }

        if (channels == 2 && sideLbrrMask != 0 && silkFrames > 1)
        {
            EncodePdf(ref enc, silkFrames == 2 ? SilkTables.LbrrFlags40Pdf : SilkTables.LbrrFlags60Pdf, sideLbrrMask);
        }
    }

    /// <summary>
    /// Writes the LBRR frames in the decoder's interleaved order (RFC 6716 §4.2.5).
    /// </summary>
    /// <param name="enc">The range encoder for this Opus frame.</param>
    /// <param name="channels">The SILK layer channel count, 1 or 2.</param>
    /// <param name="silkFrames">The number of 20 ms intervals, 1 to 3.</param>
    /// <param name="midLbrrMask">The mid channel's LBRR bits.</param>
    /// <param name="sideLbrrMask">The side channel's LBRR bits.</param>
    /// <param name="predictionIndices">Six stereo indices per interval, in <c>[interval][channel][0..2]</c> order.</param>
    /// <param name="midOnlyFlags">One mid-only flag per interval.</param>
    /// <param name="writer">Writes each frame's side information and excitation.</param>
    public static void EncodeFrames(
        ref RangeEncoder enc,
        int channels,
        int silkFrames,
        int midLbrrMask,
        int sideLbrrMask,
        ReadOnlySpan<byte> predictionIndices,
        ReadOnlySpan<bool> midOnlyFlags,
        ISilkLbrrFrameWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        for (int f = 0; f < silkFrames; f++)
        {
            bool midCoded = ((midLbrrMask >> f) & 1) != 0;
            bool sideCoded = channels == 2 && ((sideLbrrMask >> f) & 1) != 0;

            if (midCoded)
            {
                if (channels == 2)
                {
                    SilkStereoEncoder.EncodePrediction(ref enc, predictionIndices.Slice(f * 6, 6));
                    if (!sideCoded)
                    {
                        SilkStereoEncoder.EncodeMidOnly(ref enc, midOnlyFlags[f]);
                    }
                }

                bool conditional = f > 0 && ((midLbrrMask >> (f - 1)) & 1) != 0;
                writer.WriteLbrrFrame(ref enc, f, 0, conditional);
            }

            if (sideCoded)
            {
                bool conditional = f > 0 && ((sideLbrrMask >> (f - 1)) & 1) != 0;
                writer.WriteLbrrFrame(ref enc, f, 1, conditional);
            }
        }
    }

    /// <summary>Encodes one symbol whose probability mass is given as a forward PDF out of 256.</summary>
    private static void EncodePdf(ref RangeEncoder enc, ReadOnlySpan<byte> pdf, int symbol)
    {
        uint fl = 0;
        for (int k = 0; k < symbol; k++)
        {
            fl += pdf[k];
        }

        enc.Encode(fl, fl + pdf[symbol], 256);
    }
}
