// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Decoder;

/// <summary>
/// Drives the SILK bitstream decoder, synthesis and output resampler for the top-level decoder,
/// producing normalized interleaved PCM at the API sample rate and channel count.
/// </summary>
/// <remarks>
/// <para>
/// This is the streaming equivalent of the SILK output path in RFC 6716 Appendix A
/// <c>silk/dec_API.c</c>. libopus feeds its resampler one extra history sample for a mono
/// stream (the stereo mid buffer) and therefore delays mono by one internal sample relative to
/// stereo; this class reproduces that buffering explicitly and constructs the resampler with
/// the stereo calibration, so a mono stream and the same stream decoded as stereo stay aligned
/// with libopus at every API rate.
/// </para>
/// <para>
/// A change of internal sample rate re-creates the resamplers, which matches libopus's
/// <c>silk_decoder_set_fs</c> resetting the resampler state. Everything is allocated in the
/// constructor, so decoding allocates nothing after construction.
/// </para>
/// </remarks>
internal sealed class SilkOutput
{
    private const int MaxInternalSamples = 16 * 60;
    private const int MaxInternalChannels = 2;

    private readonly AudioKernelTier _tier = AudioKernels.Select();
    private readonly int _apiRate;
    private readonly int _apiChannels;
    private readonly SilkDecoder _decoder = new();
    private readonly SilkSynthesis _synthesis = new();
    private readonly SilkFrameResult _frameResult = new();
    private readonly short[] _internalPcm = new short[MaxInternalSamples * MaxInternalChannels];
    private readonly short[] _channel0 = new short[MaxInternalSamples];
    private readonly short[] _channel1 = new short[MaxInternalSamples];
    private readonly short[] _feed0 = new short[MaxInternalSamples + 1];
    private readonly short[] _feed1 = new short[MaxInternalSamples];
    private readonly short[] _resampled0;
    private readonly short[] _resampled1;
    private readonly SilkResampler[] _leftByRate = new SilkResampler[3];
    private readonly SilkResampler[] _rightByRate = new SilkResampler[3];

    private SilkResampler? _left;
    private SilkResampler? _right;
    private int _resamplerRate;

    // True while the right resampler's state is known to equal the left one's (both were reset
    // together and only mono frames, which feed both identically, have been processed since).
    // The right resampler is then not run for mono frames and is synchronized when needed.
    private bool _rightMirrorsLeft = true;
    private short _mid1;
    private int _internalRate;
    private int _internalChannels;

    /// <inheritdoc cref="SilkOutput"/>
    /// <param name="apiRate">API sample rate: 8000, 12000, 16000, 24000 or 48000 Hz.</param>
    /// <param name="apiChannels">API channel count: 1 or 2.</param>
    internal SilkOutput(int apiRate, int apiChannels)
    {
        _apiRate = apiRate;
        _apiChannels = apiChannels;
        int maxApiSamples = apiRate * 60 / 1000;
        _resampled0 = new short[maxApiSamples];
        _resampled1 = new short[maxApiSamples];

        // Every internal rate is prepared up front so that a bandwidth change never allocates.
        for (int index = 0; index < _leftByRate.Length; index++)
        {
            int internalRate = InternalRate(index);
            _leftByRate[index] = new SilkResampler(internalRate, apiRate, channels: 2, _tier);
            _rightByRate[index] = new SilkResampler(internalRate, apiRate, channels: 2, _tier);
        }
    }

    /// <summary>Clears all SILK and resampler state, as RFC 6716 §4.5.2 requires.</summary>
    internal void Reset()
    {
        _decoder.Reset();
        _synthesis.Reset();
        _left?.Reset();
        _right?.Reset();
        _rightMirrorsLeft = true;
        _mid1 = 0;
        _internalRate = 0;
        _internalChannels = 0;
    }

    /// <summary>
    /// Decodes the SILK layer of one Opus frame and writes normalized interleaved PCM at the API
    /// rate.
    /// </summary>
    /// <param name="dec">Range decoder positioned at the start of the frame's SILK data.</param>
    /// <param name="internalRateHz">SILK internal rate: 8000, 12000 or 16000 Hz.</param>
    /// <param name="internalChannels">Coded SILK channels: 1 or 2.</param>
    /// <param name="frameDurationMs">Opus frame duration: 10, 20, 40 or 60 ms.</param>
    /// <param name="pcm">
    /// Destination for <c>frameDurationMs * apiRate / 1000</c> interleaved API-rate samples; it
    /// must hold that many samples for every API channel.
    /// </param>
    /// <returns>The number of samples per channel written.</returns>
    internal int Decode(
        ref RangeDecoder dec,
        int internalRateHz,
        int internalChannels,
        int frameDurationMs,
        Span<float> pcm)
    {
        int internalSamples = internalRateHz * frameDurationMs / 1000;
        int outputSamples = _apiRate * frameDurationMs / 1000;
        if (pcm.Length < outputSamples * _apiChannels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {outputSamples * _apiChannels} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        _decoder.DecodeFrame(ref dec, internalRateHz, internalChannels, frameDurationMs, _frameResult);
        _internalRate = internalRateHz;
        _internalChannels = internalChannels;

        short heldMid = _mid1;
        int internalCount = internalSamples * internalChannels;
        _synthesis.Synthesize(_frameResult, _internalPcm.AsSpan(0, internalCount));
        _mid1 = _synthesis.LastMidSample;
        return WriteOutput(internalSamples, internalChannels, outputSamples, heldMid, pcm);
    }

    /// <summary>
    /// Conceals a lost Opus frame with the SILK packet-loss algorithm and writes normalized PCM
    /// at the API rate.
    /// </summary>
    /// <param name="frameDurationMs">Duration of the lost frame: 10, 20, 40 or 60 ms.</param>
    /// <param name="pcm">Destination for normalized interleaved PCM at the API rate.</param>
    /// <returns>The number of samples per channel written.</returns>
    internal int DecodeLost(int frameDurationMs, Span<float> pcm)
    {
        int outputSamples = _apiRate * frameDurationMs / 1000;

        // The synthesizer's geometry follows the rate of the last synthesized frame. After a mode
        // transition those can differ from the last decoded packet's rate, and concealing with the
        // stale rate asks the PLC for a differently sized frame. Follow the synthesis rate.
        if (_synthesis.RateHz != 0)
        {
            _internalRate = _synthesis.RateHz;
            _internalChannels = _synthesis.Channels;
        }

        if (_internalRate == 0 || _internalChannels == 0)
        {
            pcm[..(outputSamples * _apiChannels)].Clear();
            return outputSamples;
        }

        int internalSamples = _internalRate * frameDurationMs / 1000;
        short heldMid = _mid1;
        _synthesis.Conceal(
            frameDurationMs, _internalPcm.AsSpan(0, internalSamples * _internalChannels));
        _mid1 = _synthesis.LastMidSample;
        _decoder.ResetGainClampAfterLoss();
        return WriteOutput(internalSamples, _internalChannels, outputSamples, heldMid, pcm);
    }

    /// <summary>
    /// Decodes the in-band FEC (LBRR) frames of this packet and writes normalized PCM at the API
    /// rate, concealing intervals that carry no LBRR frame.
    /// </summary>
    /// <param name="dec">Range decoder positioned at the start of the frame's SILK data.</param>
    /// <param name="internalRateHz">SILK internal rate: 8000, 12000 or 16000 Hz.</param>
    /// <param name="internalChannels">Coded SILK channels: 1 or 2.</param>
    /// <param name="frameDurationMs">Opus frame duration: 10, 20, 40 or 60 ms.</param>
    /// <param name="pcm">Destination for normalized interleaved PCM at the API rate.</param>
    /// <returns>The number of samples per channel written.</returns>
    internal int DecodeFec(
        ref RangeDecoder dec,
        int internalRateHz,
        int internalChannels,
        int frameDurationMs,
        Span<float> pcm)
    {
        int outputSamples = _apiRate * frameDurationMs / 1000;
        int internalSamples = internalRateHz * frameDurationMs / 1000;
        short heldMid = _mid1;
        _decoder.DecodeFrame(ref dec, internalRateHz, internalChannels, frameDurationMs, _frameResult, lbrrOnly: true);
        _internalRate = internalRateHz;
        _internalChannels = internalChannels;
        _synthesis.SynthesizeFec(
            _frameResult, _internalPcm.AsSpan(0, internalSamples * internalChannels));
        _mid1 = _synthesis.LastMidSample;
        return WriteOutput(internalSamples, internalChannels, outputSamples, heldMid, pcm);
    }

    /// <summary>Feeds the internal-rate PCM through the mono buffering and the resamplers.</summary>
    /// <param name="internalSamples">Samples per channel at the internal rate.</param>
    /// <param name="internalChannels">Coded channels: 1 or 2.</param>
    /// <param name="outputSamples">Samples per channel expected at the API rate.</param>
    /// <param name="heldMid">The mid-history sample held from the previous frame.</param>
    /// <param name="pcm">Destination for normalized interleaved PCM at the API rate.</param>
    /// <returns>The number of samples per channel written.</returns>
    private int WriteOutput(
        int internalSamples, int internalChannels, int outputSamples, short heldMid, Span<float> pcm) =>
        _tier switch
        {
            AudioKernelTier.Avx2Fma => WriteOutput<Avx2FmaKernels>(internalSamples, internalChannels, outputSamples, heldMid, pcm),
            AudioKernelTier.AdvSimdArm64 => WriteOutput<AdvSimdArm64Kernels>(internalSamples, internalChannels, outputSamples, heldMid, pcm),
            AudioKernelTier.Vector128 => WriteOutput<Vector128Kernels>(internalSamples, internalChannels, outputSamples, heldMid, pcm),
            _ => WriteOutput<ScalarKernels>(internalSamples, internalChannels, outputSamples, heldMid, pcm),
        };

    private int WriteOutput<TK>(
        int internalSamples, int internalChannels, int outputSamples, short heldMid, Span<float> pcm)
        where TK : struct, ISilkKernels
    {
        long profileStart = SilkPerf4Profile.Start();
        if (internalChannels == 2)
        {
            TK.Deinterleave(_internalPcm, _channel0, _channel1, internalSamples);
        }
        else
        {
            _internalPcm.AsSpan(0, internalSamples).CopyTo(_channel0);
        }

        EnsureResamplers(_internalRate);

        ReadOnlySpan<short> feed0;
        ReadOnlySpan<short> feed1;
        if (internalChannels == 1)
        {
            // RFC 6716 Appendix A, dec_API.c: the mono path prepends the two-sample mid history
            // and feeds from its second entry, then holds the last two samples for the next
            // frame. That is exactly a one-internal-sample delay of the mono signal.
            _feed0[0] = heldMid;
            _channel0.AsSpan(0, internalSamples).CopyTo(_feed0.AsSpan(1));
            feed0 = _feed0.AsSpan(0, internalSamples);
            feed1 = feed0;
        }
        else if (_apiChannels == 1)
        {
            // A stereo stream decoded to mono uses the mid signal without the MS-to-LR
            // conversion, exactly as dec_API.c's buffering branch does. The stereo synthesis
            // output is already history-delayed, so no extra buffering is applied.
            TK.AverageShorts(_channel0, _channel1, _feed0, internalSamples);

            feed0 = _feed0.AsSpan(0, internalSamples);
            feed1 = feed0;
        }
        else
        {
            _channel0.AsSpan(0, internalSamples).CopyTo(_feed0);
            _channel1.AsSpan(0, internalSamples).CopyTo(_feed1);
            feed0 = _feed0.AsSpan(0, internalSamples);
            feed1 = _feed1.AsSpan(0, internalSamples);
        }

        SilkPerf4Profile.Stop(23, profileStart);
        int produced = _left!.OutputCount(internalSamples);
        if (produced != outputSamples)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The SILK resampler produced {produced} samples, expected {outputSamples}."));
        }

        // The right resampler only matters for a stereo output. Until a coded-stereo frame
        // arrives it would hold exactly the left state, so it is synchronized then instead of
        // being run on identical mono data.
        bool runRight = _apiChannels == 2 && (internalChannels == 2 || !_rightMirrorsLeft);
        if (runRight && _rightMirrorsLeft)
        {
            _right!.CopyStateFrom(_left);
        }

        if (runRight)
        {
            _rightMirrorsLeft = false;
        }

        profileStart = SilkPerf4Profile.Start();
        _left.Process(feed0, _resampled0.AsSpan(0, produced));
        if (runRight)
        {
            _right!.Process(feed1, _resampled1.AsSpan(0, produced));
        }

        SilkPerf4Profile.Stop(24, profileStart);
        profileStart = SilkPerf4Profile.Start();

        const float scale = 1f / 32768f;
        if (_apiChannels == 2)
        {
            TK.InterleaveToFloat(
                _resampled0, internalChannels == 2 ? _resampled1 : _resampled0, pcm, produced, scale);
        }
        else
        {
            TK.ShortsToFloat(_resampled0, pcm, produced, scale);
        }

        SilkPerf4Profile.Stop(25, profileStart);
        return produced;
    }

    /// <summary>Selects the resampler for the internal rate and resets it on a rate change.</summary>
    /// <param name="internalRateHz">The SILK internal rate of the frame being decoded.</param>
    /// <remarks>
    /// RFC 6716 Appendix A, <c>silk_decoder_set_fs</c>: a change of internal rate reinitializes
    /// the resampler. The calibration used here is always the stereo one, because the explicit
    /// one-sample mid buffering above already supplies the extra mono delay.
    /// </remarks>
    private void EnsureResamplers(int internalRateHz)
    {
        if (_resamplerRate == internalRateHz)
        {
            return;
        }

        int index = RateIndex(internalRateHz);
        _left = _leftByRate[index];
        _right = _rightByRate[index];
        _left.Reset();
        _right.Reset();
        _rightMirrorsLeft = true;
        _resamplerRate = internalRateHz;
    }

    /// <summary>Maps an internal rate to its table index and back.</summary>
    /// <param name="index">Table index 0 to 2, or an internal rate when used as a lookup.</param>
    /// <returns>The internal rate for an index, or the index for a rate.</returns>
    private static int InternalRate(int index) => index switch
    {
        0 => 8000,
        1 => 12000,
        _ => 16000,
    };

    /// <summary>Returns the table index for an internal rate.</summary>
    /// <param name="internalRateHz">8000, 12000 or 16000 Hz.</param>
    /// <returns>0, 1 or 2.</returns>
    private static int RateIndex(int internalRateHz) => internalRateHz switch
    {
        8000 => 0,
        12000 => 1,
        _ => 2,
    };
}
