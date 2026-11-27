using Kiln.Audio;
using Kiln.Audio.Decoder;
using Kiln.Audio.Encoder;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// A stub SILK encoder that emits one syntactically valid, inactive, mono, wideband 20 ms frame.
/// </summary>
/// <remarks>
/// The SILK encoder lane has not landed, so the transition tests drive the real SILK decoder with
/// a frame whose symbols are written in exactly the order <see cref="SilkDecoder"/> reads them.
/// Every symbol is the one that keeps the frame inactive and silent: no VAD, no LBRR, inactive
/// frame type, zero gain deltas, the centre NLSF stage-2 index and zero pulses.
/// </remarks>
internal sealed class MockInactiveSilkEncoder : ISilkLayerEncoder
{
    /// <inheritdoc />
    public void Encode(ref RangeEncoder encoder, ReadOnlySpan<short> pcm, int frameSamples, int maxFrameBytes)
    {
        if (frameSamples != 960)
        {
            throw new NotSupportedException("The stub models only 20 ms wideband mono frames.");
        }

        // RFC 6716 §4.2.3: one VAD bit then one LBRR flag for the 20 ms frame's single interval.
        encoder.EncodeBitLogp(0, 1);
        encoder.EncodeBitLogp(0, 1);

        // §4.2.7.3: inactive frame type with the low quantization offset.
        EncodePdf(ref encoder, SilkTables.FrameTypeInactivePdf, 0);

        // §4.2.7.4: the first subframe's gain is coded independently, the rest as deltas.
        EncodePdf(ref encoder, SilkTables.GainMsbPdf[(int)SilkSignalType.Inactive], 0);
        EncodePdf(ref encoder, SilkTables.GainLsbPdf, 0);
        for (int k = 1; k < 4; k++)
        {
            EncodePdf(ref encoder, SilkTables.GainDeltaPdf, 4); // delta 4 keeps the gain at zero
        }

        // §4.2.7.5: stage-1 index 0 selects the stage-2 PDF row below; the centre index is 4.
        EncodePdf(ref encoder, SilkTables.NlsfStage1WbInactivePdf, 0);
        string selection = SilkTables.NlsfStage2SelectWb[0];
        for (int k = 0; k < 16; k++)
        {
            EncodePdf(ref encoder, SilkTables.NlsfStage2WbPdf[selection[k] - 'i'], 4);
        }

        EncodePdf(ref encoder, SilkTables.NlsfInterpolationPdf, 0);

        // §4.2.7.7 and §4.2.7.8: seed, inactive rate level, then zero pulses in every shell block.
        EncodePdf(ref encoder, SilkTables.SeedPdf, 0);
        EncodePdf(ref encoder, SilkTables.RateLevelInactivePdf, 0);
        int blocks = SilkTables.ShellBlockCount[2][1]; // wideband, 20 ms
        for (int block = 0; block < blocks; block++)
        {
            EncodePdf(ref encoder, SilkTables.PulseCountPdf[0], 0);
        }
    }

    private static void EncodePdf(ref RangeEncoder encoder, ReadOnlySpan<byte> pdf, int symbol)
    {
        uint lower = 0;
        for (int i = 0; i < symbol; i++)
        {
            lower += pdf[i];
        }

        uint upper = lower + pdf[symbol];
        encoder.Encode(lower, upper, 256);
    }
}

/// <summary>
/// A stub redundant CELT encoder that emits the two-byte CELT silence frame.
/// </summary>
/// <remarks>
/// A silent CELT frame reads no band data, so it is valid at every end band, including the
/// wideband limit of a SILK-only packet that the current CELT encoder cannot band-limit. The
/// final range is obtained by decoding the frame with the library's own CELT decoder, which is
/// exactly what the top-level decoder will reconstruct.
/// </remarks>
internal sealed class MockSilentRedundantCeltEncoder : IRedundantCeltEncoder
{
    private readonly CeltOutput _celt = new(48000, 1);
    private readonly float[] _pcm = new float[240];
    private uint _finalRange;

    /// <inheritdoc />
    public uint FinalRange => _finalRange;

    /// <inheritdoc />
    public int FrameBytes => 2;

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<short> pcm, int endBand, Span<byte> destination)
    {
        destination.Clear();
        if (destination.Length < 2)
        {
            throw new ArgumentException("A redundant frame is at least two bytes.", nameof(destination));
        }

        // The largest possible entropy value decodes as the silence flag in a fresh range decoder.
        destination[0] = 0xFF;
        destination[1] = 0xFF;

        _celt.Reset();
        _finalRange = _celt.DecodeRedundant(destination, endBand, 1, _pcm);
    }
}
