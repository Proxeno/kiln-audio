// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Decoder;

/// <summary>
/// Drives the CELT bitstream decoder and synthesizer for the top-level decoder and converts the
/// 48 kHz synthesis to the API sample rate and channel count.
/// </summary>
/// <remarks>
/// <para>
/// CELT always operates at 48 kHz internally (RFC 6716 §4.3). libopus drops to the API rate in
/// its de-emphasis stage by keeping every <c>48000 / Fs</c>-th sample of the 48 kHz filtered
/// signal; this class does the same after <see cref="CeltSynthesizer.Synthesize"/>, which keeps
/// the de-emphasis state running at 48 kHz exactly as the reference does.
/// </para>
/// <para>
/// All scratch storage is allocated in the constructor, so decoding and concealment allocate
/// nothing after construction.
/// </para>
/// </remarks>
internal sealed class CeltOutput
{
    private const int MaxCeltSamples48 = 8 * CeltTables.ShortBlockSamples;

    private readonly int _apiChannels;
    private readonly int _downsample;
    private readonly CeltDecoder _decoder;
    private readonly CeltSynthesizer _synthesizer;
    private readonly CeltFrameResult _frame = new();
    private readonly float[] _pcm48;
    private readonly float[] _windowSquared;

    /// <inheritdoc cref="CeltOutput"/>
    /// <param name="apiRate">API sample rate: 8000, 12000, 16000, 24000 or 48000 Hz.</param>
    /// <param name="apiChannels">API channel count: 1 or 2.</param>
    internal CeltOutput(int apiRate, int apiChannels)
    {
        _apiChannels = apiChannels;
        _downsample = 48000 / apiRate;
        _decoder = new CeltDecoder(apiChannels);
        _synthesizer = new CeltSynthesizer(apiChannels, downsample: _downsample);
        _pcm48 = new float[MaxCeltSamples48 * apiChannels];

        float[] window = CeltWindow.Create(CeltWindow.DefaultOverlap);
        _windowSquared = new float[window.Length];
        for (int i = 0; i < window.Length; i++)
        {
            _windowSquared[i] = window[i] * window[i];
        }

        Reset();
    }

    /// <summary>Gets the squared CELT window used for transition cross-fades.</summary>
    internal ReadOnlySpan<float> WindowSquared => _windowSquared;

    /// <summary>Clears every cross-frame bitstream and synthesis state (RFC 6716 §4.5.2).</summary>
    internal void Reset()
    {
        _decoder.Reset();
        _synthesizer.Reset();
    }

    /// <summary>
    /// Decodes one CELT frame and writes normalized interleaved PCM at the API rate.
    /// </summary>
    /// <param name="dec">Range decoder positioned at the start of the frame's CELT data.</param>
    /// <param name="frameBytes">
    /// Number of bytes in the frame's range-coded payload. For a hybrid frame this is the frame
    /// length after the redundant CELT frame at the end has been removed (RFC 6716 §4.5.1.3).
    /// </param>
    /// <param name="lm">Base-2 logarithm of the number of short blocks (0 to 3).</param>
    /// <param name="startBand">First coded band: 0 for CELT-only, 17 for a hybrid frame.</param>
    /// <param name="endBand">One past the last coded band, from the packet bandwidth.</param>
    /// <param name="streamChannels">Number of coded channels: 1 or 2.</param>
    /// <param name="pcm">
    /// Destination for <c>(CeltTables.ShortBlockSamples &lt;&lt; lm) / Downsample</c> interleaved
    /// samples per channel; it must hold that many samples for every API channel.
    /// </param>
    /// <returns>The number of samples per channel written.</returns>
    internal int Decode(
        ref RangeDecoder dec,
        int frameBytes,
        int lm,
        int startBand,
        int endBand,
        int streamChannels,
        Span<float> pcm)
    {
        _decoder.DecodeFrame(ref dec, frameBytes, lm, startBand, endBand, streamChannels, _frame);
        if (_downsample == 1)
        {
            // 48 kHz output: synthesize straight into the caller's buffer, no intermediate copy.
            EnsureCapacity(_frame.SampleCount, pcm);
            _synthesizer.Synthesize(_frame, pcm);
            return _frame.SampleCount;
        }

        _synthesizer.Synthesize(_frame, _pcm48.AsSpan(0, _frame.SampleCount * _apiChannels));
        return WriteDecimated(_frame.SampleCount, pcm);
    }

    /// <summary>
    /// Decodes a redundant 5 ms CELT frame from its own range-coded bytes (RFC 6716 §4.5.1.4).
    /// </summary>
    /// <param name="data">The redundant frame's bytes.</param>
    /// <param name="endBand">One past the last coded band, from the current packet's bandwidth.</param>
    /// <param name="streamChannels">Number of coded channels: 1 or 2.</param>
    /// <param name="pcm">Destination for 5 ms of normalized interleaved PCM at the API rate.</param>
    /// <returns>The redundant frame's final range.</returns>
    internal uint DecodeRedundant(
        ReadOnlySpan<byte> data, int endBand, int streamChannels, Span<float> pcm)
    {
        var dec = new RangeDecoder(data);
        Decode(ref dec, data.Length, lm: 1, startBand: 0, endBand, streamChannels, pcm);
        return dec.Rng;
    }

    /// <summary>
    /// Decodes the two-byte silence frame that flushes the CELT overlap on a hybrid-to-SILK
    /// transition (RFC 6716 §4.5).
    /// </summary>
    /// <param name="endBand">One past the last coded band, from the current packet's bandwidth.</param>
    /// <param name="streamChannels">Number of coded channels: 1 or 2.</param>
    /// <param name="pcm">Destination for 2.5 ms of normalized interleaved PCM at the API rate.</param>
    /// <returns>The number of samples per channel written.</returns>
    internal int DecodeSilenceFlush(int endBand, int streamChannels, Span<float> pcm)
    {
        ReadOnlySpan<byte> silence = stackalloc byte[] { 0xFF, 0xFF };
        var dec = new RangeDecoder(silence);
        return Decode(ref dec, silence.Length, lm: 0, startBand: 0, endBand, streamChannels, pcm);
    }

    /// <summary>
    /// Produces concealment for a lost frame through the CELT <c>DecodeLost</c> hook.
    /// </summary>
    /// <param name="lm">Base-2 logarithm of the number of short blocks for the lost frame.</param>
    /// <param name="startBand">First band to generate: 0 for CELT-only, 17 for hybrid.</param>
    /// <param name="pcm">Destination for normalized interleaved PCM at the API rate.</param>
    /// <returns>The number of samples per channel written.</returns>
    internal int DecodeLost(int lm, int startBand, Span<float> pcm)
    {
        int samples48 = CeltTables.ShortBlockSamples << lm;
        if (_downsample == 1)
        {
            EnsureCapacity(samples48, pcm);
            _synthesizer.DecodeLost(lm, startBand, pcm);
            return samples48;
        }

        _synthesizer.DecodeLost(lm, startBand, _pcm48.AsSpan(0, samples48 * _apiChannels));
        return WriteDecimated(samples48, pcm);
    }

    /// <summary>Keeps every <c>Downsample</c>-th 48 kHz sample, as the reference de-emphasis does.</summary>
    /// <param name="samples48">Number of 48 kHz samples synthesized per channel.</param>
    /// <param name="pcm">Destination for the decimated interleaved output.</param>
    /// <returns>The number of output samples per channel.</returns>
    private int WriteDecimated(int samples48, Span<float> pcm)
    {
        int outputSamples = samples48 / _downsample;
        EnsureCapacity(outputSamples, pcm);

        for (int i = 0; i < outputSamples; i++)
        {
            int source = i * _downsample * _apiChannels;
            int destination = i * _apiChannels;
            for (int channel = 0; channel < _apiChannels; channel++)
            {
                pcm[destination + channel] = _pcm48[source + channel];
            }
        }

        return outputSamples;
    }

    private void EnsureCapacity(int samplesPerChannel, Span<float> pcm)
    {
        if (pcm.Length < samplesPerChannel * _apiChannels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {samplesPerChannel * _apiChannels} samples but holds {pcm.Length}."),
                nameof(pcm));
        }
    }
}
