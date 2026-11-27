// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Silk;

/// <summary>
/// Decodes the SILK layer of one Opus frame into its quantized parameters
/// (RFC 6716 §4.2.2 through §4.2.7.8.5).
/// </summary>
/// <remarks>
/// <para>
/// The decoder consumes exactly the symbols the RFC places in an Opus frame's SILK layer and
/// leaves the range decoder positioned for the next frame, which is what makes the encoder's
/// final range a bit-exact check on the symbol sequence. Synthesis (LTP, LPC and the
/// excitation-to-PCM step) is out of scope here; only the parameters are produced.
/// </para>
/// <para>
/// The header bits, LBRR frames and regular frames are all read from one range decoder per
/// Opus frame (RFC 6716 §4.2.2). The only state that carries across Opus frames is the clamp
/// of an independently coded gain (RFC 6716 §4.2.7.4) and the previous stereo weights, neither
/// of which affects the symbols, so a caller may reset between Opus frames without changing the
/// final range.
/// </para>
/// <para>
/// A change of internal rate resets the decoder (RFC 6716 §4.5.2). A change of channel count at
/// the same rate is not a mode change: the mid (channel 0) state is kept, the side channel starts
/// fresh, and the stereo weights are cleared (RFC 8251 §3; Appendix A, <c>silk/dec_API.c</c>).
/// </para>
/// </remarks>
internal sealed class SilkDecoder
{
    private const int MaxChannels = SilkFrameResult.MaxChannels;

    /// <summary>Approximately 0.1 in Q16, used to interpolate stereo weights (RFC 6716 §4.2.7.1).</summary>
    private const int StereoInterpolationScale = 6554;

    /// <summary>
    /// Cross-frame state that affects symbol decoding for one channel and one frame type.
    /// </summary>
    private struct ChannelState
    {
        /// <summary>Previous primary pitch lag, or -1 (RFC 6716 §4.2.7.6.1).</summary>
        public int PreviousLag;

        /// <summary>Previous first stereo prediction weight, Q13 (RFC 6716 §4.2.7.1).</summary>
        public int StereoWeight0Q13;

        /// <summary>Previous second stereo prediction weight, Q13 (RFC 6716 §4.2.7.1).</summary>
        public int StereoWeight1Q13;

        public void Clear()
        {
            PreviousLag = -1;
            StereoWeight0Q13 = 0;
            StereoWeight1Q13 = 0;
        }
    }

    private readonly ChannelState[] _regularState = new ChannelState[MaxChannels];
    private readonly ChannelState[] _lbrrState = new ChannelState[MaxChannels];

    /// <summary>
    /// The gain index of the most recent coded subframe in each channel, or -1 when no value is
    /// available. LBRR and regular frames share it, because it is a per-channel value
    /// (RFC 6716 §4.2.7.4).
    /// </summary>
    private readonly int[] _previousLogGain = new int[MaxChannels];

    /// <summary>
    /// PERF-4: the stage-2 PDF selector letters, pre-reduced to 0-based PDF indices.
    /// </summary>
    /// <remarks>
    /// The tables in <see cref="SilkTables"/> store the reference's selector as a string of
    /// letters; converting them once removes a string index, a character load and a subtraction
    /// from every NLSF coefficient of every frame.
    /// </remarks>
    private static readonly byte[][] NlsfSelectWb = BuildNlsfSelect(SilkTables.NlsfStage2SelectWb, 'i');
    private static readonly byte[][] NlsfSelectNbMb = BuildNlsfSelect(SilkTables.NlsfStage2SelectNbMb, 'a');

    private int _rateHz;
    private int _channels;
    private int _bandwidthIndex;
    private int _subframes;

    /// <summary>Creates a decoder in its reset state.</summary>
    public SilkDecoder() => Reset();

    /// <summary>
    /// Clears every piece of cross-frame state (RFC 6716 §4.5.2), including the stereo weight
    /// memory that RFC 8251 §3 requires to be reinitialized on a mode switch.
    /// </summary>
    public void Reset()
    {
        _rateHz = 0;
        _channels = 0;
        _bandwidthIndex = 0;
        _subframes = 0;
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            _regularState[channel].Clear();
            _lbrrState[channel].Clear();
            _previousLogGain[channel] = -1;
        }
    }

    /// <summary>
    /// Decodes the complete SILK layer of one Opus frame.
    /// </summary>
    /// <param name="dec">The range decoder for this Opus frame, positioned at its first byte.</param>
    /// <param name="internalRateHz">The internal SILK sample rate: 8000, 12000 or 16000 Hz.</param>
    /// <param name="channels">The channel count of the SILK layer: 1 or 2.</param>
    /// <param name="frameDurationMs">The Opus frame duration: 10, 20, 40 or 60 ms.</param>
    /// <param name="result">Caller-owned storage that receives the decoded parameters.</param>
    /// <param name="lbrrOnly">When true, decode only the LBRR (in-band FEC) frames.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The rate, channel count or duration is not one of the RFC 6716 §2 Table 2 values.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <remarks>
    /// A change of internal rate or channel count resets the decoder, as RFC 6716 §4.5.2
    /// requires for a bandwidth or mode transition.
    /// </remarks>
    public void DecodeFrame(
        ref RangeDecoder dec,
        int internalRateHz,
        int channels,
        int frameDurationMs,
        SilkFrameResult result,
        bool lbrrOnly = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        long profileStart = SilkPerf4Profile.Start();
        int bandwidthIndex = internalRateHz switch
        {
            8000 => 0,
            12000 => 1,
            16000 => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(internalRateHz), internalRateHz, "The SILK internal rate must be 8000, 12000 or 16000 Hz."),
        };
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "The SILK layer has one or two channels.");
        }

        if (frameDurationMs is not (10 or 20 or 40 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameDurationMs), frameDurationMs, "The Opus frame duration must be 10, 20, 40 or 60 ms.");
        }

        // RFC 6716 §4.5.2: a bandwidth or mode change resets the SILK state. A reset decoder
        // (_rateHz == 0) is already clear.
        if (_rateHz != 0 && _rateHz != internalRateHz)
        {
            for (int channel = 0; channel < MaxChannels; channel++)
            {
                _regularState[channel].Clear();
                _lbrrState[channel].Clear();
                _previousLogGain[channel] = -1;
            }
        }
        else if (_rateHz != 0 && _channels != channels)
        {
            // A channel-count change at the same rate is not a mode change. The reference
            // implementation keeps the mid channel's decoder state (Appendix A, silk/dec_API.c:
            // only channel_state[1] is reinitialized on mono to stereo), starts the side channel
            // fresh, and clears the stereo prediction state (RFC 8251 §3).
            _regularState[0].StereoWeight0Q13 = 0;
            _regularState[0].StereoWeight1Q13 = 0;
            _lbrrState[0].StereoWeight0Q13 = 0;
            _lbrrState[0].StereoWeight1Q13 = 0;
            _regularState[1].Clear();
            _lbrrState[1].Clear();
            _previousLogGain[1] = -1;
        }

        _rateHz = internalRateHz;
        _channels = channels;
        _bandwidthIndex = bandwidthIndex;
        _subframes = frameDurationMs == 10 ? 2 : 4;
        result.Begin(internalRateHz, channels, frameDurationMs);
        int silkFrames = result.SilkFrameCount;

        // RFC 6716 §4.2.3: one VAD bit per SILK frame, then one LBRR flag, for the mid (or
        // mono) channel first and then the side channel.
        int midVadMask = 0;
        int sideVadMask = 0;
        for (int f = 0; f < silkFrames; f++)
        {
            if (DecodeFlag(ref dec))
            {
                midVadMask |= 1 << f;
                result.SetVadFlag(f, 0, true);
            }
        }

        bool midHasLbrr = DecodeFlag(ref dec);
        result.SetHasLbrr(0, midHasLbrr);
        if (channels == 2)
        {
            for (int f = 0; f < silkFrames; f++)
            {
                if (DecodeFlag(ref dec))
                {
                    sideVadMask |= 1 << f;
                    result.SetVadFlag(f, 1, true);
                }
            }
        }

        bool sideHasLbrr = channels == 2 && DecodeFlag(ref dec);
        if (channels == 2)
        {
            result.SetHasLbrr(1, sideHasLbrr);
        }

        // RFC 6716 §4.2.4: 40 and 60 ms frames follow their LBRR flags with a per-frame mask.
        int midLbrrMask = midHasLbrr ? DecodeLbrrMask(ref dec, silkFrames) : 0;
        int sideLbrrMask = sideHasLbrr ? DecodeLbrrMask(ref dec, silkFrames) : 0;

        long stageStart = SilkPerf4Profile.Start();
        DecodeLbrrFrames(ref dec, result, silkFrames, midLbrrMask, sideLbrrMask);
        SilkPerf4Profile.Stop(15, stageStart);
        if (lbrrOnly)
        {
            // In-band FEC decodes only the LBRR frames; the regular frames are decoded when this
            // packet is submitted normally. Leaving them unparsed keeps the cross-frame state
            // exactly where the reference leaves it after a FLAG_DECODE_LBRR pass.
            SilkPerf4Profile.Stop(13, profileStart);
            return;
        }

        stageStart = SilkPerf4Profile.Start();
        DecodeRegularFrames(ref dec, result, silkFrames, midVadMask, sideVadMask);
        SilkPerf4Profile.Stop(16, stageStart);
        SilkPerf4Profile.Stop(13, profileStart);
    }

    /// <summary>
    /// Reads the transition redundancy that follows the SILK layer of a SILK-only Opus frame
    /// (RFC 6716 §4.5.1.1, §4.5.1.2 and §4.5.1.3).
    /// </summary>
    /// <param name="dec">
    /// The frame's range decoder, positioned immediately after <see cref="DecodeFrame"/> for the
    /// same frame.
    /// </param>
    /// <param name="frameBytes">The number of bytes in the frame's range-coded payload.</param>
    /// <returns>The redundancy, or <see cref="OpusRedundancyInfo.None"/> when there is none.</returns>
    /// <remarks>
    /// For SILK-only frames the presence is implicit: at least 17 bits must remain after the
    /// SILK layer, and the check itself consumes nothing. The position flag then uses the
    /// uniform Table 65 PDF. The redundant frame is exactly the whole bytes that remain, which
    /// the caller hands to its own CELT range decoder (RFC 6716 §4.5.1.3).
    /// </remarks>
    internal static OpusRedundancyInfo DecodeRedundancy(ref RangeDecoder dec, int frameBytes)
    {
        // RFC 6716 §4.5.1.1: ec_tell() after the SILK layer, with at least 17 bits remaining.
        if ((frameBytes * 8) - dec.Tell() < OpusRedundancyInfo.MinimumBits)
        {
            return OpusRedundancyInfo.None;
        }

        // RFC 6716 §4.5.1.2, Table 65: the redundancy position flag.
        bool celtToSilk = dec.DecodeBitLogp(1);

        // RFC 6716 §4.5.1.3: for SILK-only frames the size is every whole byte left.
        int bytes = ((frameBytes * 8) - dec.Tell()) / 8;
        return OpusRedundancyInfo.Create(celtToSilk, bytes);
    }

    /// <summary>
    /// Removes the previous-gain clamp after a lost packet (RFC 6716 Appendix A, <c>dec_API.c</c>:
    /// on <c>FLAG_PACKET_LOST</c> the reference sets every channel's <c>LastGainIndex</c> to 10).
    /// </summary>
    /// <remarks>
    /// Without this, a low gain decoded after the loss would be clamped against the pre-loss
    /// gain, so the first good frame would differ from the reference's by the narrowband energy
    /// that the concealment had been fading. The clamp is reset only for a real packet loss, not
    /// for an LBRR interval that falls back to concealment.
    /// </remarks>
    internal void ResetGainClampAfterLoss()
    {
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            _previousLogGain[channel] = 10;
        }
    }

    /// <summary>Dequantizes a gain index to a linear Q16 scale factor (RFC 6716 §4.2.7.4).</summary>
    /// <param name="logGain">The 6-bit logarithmic gain index, 0 to 63.</param>
    /// <returns>The Q16 scale factor.</returns>
    internal static int DequantizeGainQ16(int logGain) =>
        SilkTables.Log2Lin((int)(((long)0x1D1C71 * logGain) >> 16) + 2090);

    /// <summary>
    /// Decodes the pulse locations of one 16-sample shell block (RFC 6716 §4.2.7.8.3).
    /// </summary>
    /// <param name="dec">The range decoder.</param>
    /// <param name="pulseCount">The block's pulse count, 0 to 16.</param>
    /// <param name="destination">Receives 16 pulse counts; must hold at least 16 entries.</param>
    internal static void DecodeShellBlock(ref RangeDecoder dec, int pulseCount, Span<short> destination)
    {
        Span<short> block = destination[..SilkChannelFrame.ShellBlockSize];
        block.Clear();
        if (pulseCount <= 0)
        {
            return;
        }

        SplitPulses(ref dec, block, 0, SilkChannelFrame.ShellBlockSize, pulseCount);
    }

    /// <summary>Decodes one LBRR flag or VAD bit, a uniform binary symbol (RFC 6716 §4.2.3).</summary>
    private static bool DecodeFlag(ref RangeDecoder dec) => dec.DecodeBitLogp(1);

    /// <summary>Decodes the per-frame LBRR flag mask for one channel (RFC 6716 §4.2.4, Table 4).</summary>
    internal static int DecodeLbrrMask(ref RangeDecoder dec, int silkFrames) => silkFrames switch
    {
        2 => dec.DecodePdf(SilkTables.LbrrFlags40Pdf),
        3 => dec.DecodePdf(SilkTables.LbrrFlags60Pdf),
        _ => 1,
    };

    private void DecodeLbrrFrames(ref RangeDecoder dec, SilkFrameResult result, int silkFrames, int midMask, int sideMask)
    {
        int midSignalType = -1;
        int sideSignalType = -1;
        for (int f = 0; f < silkFrames; f++)
        {
            bool midCoded = (midMask & (1 << f)) != 0;
            bool sideCoded = (sideMask & (1 << f)) != 0;
            bool previousMidCoded = f > 0 && (midMask & (1 << (f - 1))) != 0;
            bool previousSideCoded = f > 0 && (sideMask & (1 << (f - 1))) != 0;

            if (midCoded)
            {
                // RFC 6716 §4.2.5: LBRR frames are always active, and a stereo mid frame still
                // carries the stereo weights and (when the side is absent) the mid-only flag.
                SilkChannelFrame frame = result.BeginLbrr(f, 0);
                DecodeSilkFrame(
                    ref dec, channel: 0, isLbrr: true, frameIndex: f, vadFlag: true,
                    previousCoded: previousMidCoded, previousSignalType: midSignalType,
                    sideActive: sideCoded, target: frame);
                midSignalType = (int)frame.SignalType;
            }

            if (sideCoded)
            {
                SilkChannelFrame frame = result.BeginLbrr(f, 1);
                DecodeSilkFrame(
                    ref dec, channel: 1, isLbrr: true, frameIndex: f, vadFlag: true,
                    previousCoded: previousSideCoded, previousSignalType: sideSignalType,
                    sideActive: true, target: frame);
                sideSignalType = (int)frame.SignalType;
            }
        }
    }

    private void DecodeRegularFrames(ref RangeDecoder dec, SilkFrameResult result, int silkFrames, int midVadMask, int sideVadMask)
    {
        bool stereo = _channels == 2;
        int midSignalType = -1;
        int sideSignalType = -1;
        bool previousSideCoded = false;

        for (int f = 0; f < silkFrames; f++)
        {
            bool midVad = ((midVadMask >> f) & 1) != 0;
            bool sideVad = ((sideVadMask >> f) & 1) != 0;

            // RFC 6716 §4.2.6: the mid frame is coded for every interval, whatever its VAD bit.
            SilkChannelFrame mid = result.BeginRegular(f, 0);
            bool midOnly = DecodeSilkFrame(
                ref dec, channel: 0, isLbrr: false, frameIndex: f, vadFlag: midVad,
                previousCoded: f > 0, previousSignalType: midSignalType,
                sideActive: stereo && sideVad, target: mid);
            midSignalType = (int)mid.SignalType;

            bool sideCoded = false;
            if (stereo)
            {
                // RFC 6716 §4.2.7.2: with an active side channel the mid-only flag is omitted
                // and the side frame is always coded; otherwise only a zero flag codes it.
                sideCoded = sideVad || !midOnly;
                if (sideCoded)
                {
                    SilkChannelFrame side = result.BeginRegular(f, 1);
                    DecodeSilkFrame(
                        ref dec, channel: 1, isLbrr: false, frameIndex: f, vadFlag: sideVad,
                        previousCoded: previousSideCoded, previousSignalType: sideSignalType,
                        sideActive: false, target: side);
                    sideSignalType = (int)side.SignalType;
                }
                else
                {
                    // No gain is available from an uncoded side frame (RFC 6716 §4.2.7.4).
                    sideSignalType = -1;
                    _previousLogGain[1] = -1;
                }
            }

            previousSideCoded = sideCoded;
        }
    }

    /// <summary>Decodes one SILK frame and returns its mid-only flag.</summary>
    /// <param name="dec">The range decoder.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    /// <param name="isLbrr">True for an LBRR frame.</param>
    /// <param name="frameIndex">Interval index within the Opus frame.</param>
    /// <param name="vadFlag">The frame's VAD bit, or true for an LBRR frame.</param>
    /// <param name="previousCoded">
    /// Whether the previous SILK frame of the same type and channel in this Opus frame was coded.
    /// </param>
    /// <param name="previousSignalType">That frame's signal type, or -1.</param>
    /// <param name="sideActive">
    /// Whether the corresponding side-channel frame is coded; it suppresses the mid-only flag.
    /// </param>
    /// <param name="target">Storage for the decoded parameters.</param>
    /// <returns>The mid-only flag of a mid channel frame; false otherwise.</returns>
    private bool DecodeSilkFrame(
        ref RangeDecoder dec,
        int channel,
        bool isLbrr,
        int frameIndex,
        bool vadFlag,
        bool previousCoded,
        int previousSignalType,
        bool sideActive,
        SilkChannelFrame target)
    {
        bool stereo = _channels == 2;
        bool isMid = channel == 0;
        ref ChannelState state = ref (isLbrr ? ref _lbrrState[channel] : ref _regularState[channel]);
        bool firstOfType = frameIndex == 0;
        bool midOnly = false;

        // RFC 6716 §4.2.7.1 and §4.2.7.2.
        if (stereo && isMid)
        {
            DecodeStereoWeights(ref dec, target, ref state);
            if (!sideActive)
            {
                midOnly = dec.DecodePdf(SilkTables.MidOnlyPdf) != 0;
            }

            target.IsMidOnly = midOnly;
        }

        // RFC 6716 §4.2.7.3, Table 9 and Table 10.
        int frameType = dec.DecodePdf(
            vadFlag ? SilkTables.FrameTypeActivePdf : SilkTables.FrameTypeInactivePdf);
        target.SignalType = (SilkSignalType)(frameType >> 1);
        target.QuantizationOffsetType = frameType & 1;

        long stageStart = SilkPerf4Profile.Start();
        DecodeGains(ref dec, target, channel, firstOfType, previousCoded);
        SilkPerf4Profile.Stop(17, stageStart);

        stageStart = SilkPerf4Profile.Start();
        DecodeNlsf(ref dec, target);
        SilkPerf4Profile.Stop(18, stageStart);

        stageStart = SilkPerf4Profile.Start();
        if (target.IsVoiced)
        {
            DecodeLtp(ref dec, target, ref state, isLbrr, firstOfType, previousCoded, previousSignalType);
        }
        else
        {
            target.LtpScalingQ14 = SilkTables.LtpScalingDefaultQ14;
        }

        SilkPerf4Profile.Stop(19, stageStart);

        stageStart = SilkPerf4Profile.Start();
        DecodeExcitation(ref dec, target);
        SilkPerf4Profile.Stop(20, stageStart);
        return midOnly;
    }

    /// <summary>Decodes the stereo prediction weights (RFC 6716 §4.2.7.1, Tables 6 and 7).</summary>
    private static void DecodeStereoWeights(ref RangeDecoder dec, SilkChannelFrame target, ref ChannelState state)
    {
        int n = dec.DecodePdf(SilkTables.StereoPredJointPdf);
        int i0 = dec.DecodePdf(SilkTables.StereoPredStage2Pdf);
        int i1 = dec.DecodePdf(SilkTables.StereoPredStage3Pdf);
        int i2 = dec.DecodePdf(SilkTables.StereoPredStage2Pdf);
        int i3 = dec.DecodePdf(SilkTables.StereoPredStage3Pdf);

        int wi0 = i0 + (3 * (n / 5));
        int wi1 = i2 + (3 * (n % 5));
        short[] weight = SilkTables.StereoWeightQ13;

        // N.B.: w1 is computed first because w0 depends on it (RFC 6716 §4.2.7.1).
        int w1 = weight[wi1]
            + ((((weight[wi1 + 1] - weight[wi1]) * StereoInterpolationScale) >> 16) * ((2 * i3) + 1));
        int w0 = weight[wi0]
            + ((((weight[wi0 + 1] - weight[wi0]) * StereoInterpolationScale) >> 16) * ((2 * i1) + 1))
            - w1;

        target.StereoWeight0Q13 = w0;
        target.StereoWeight1Q13 = w1;
        state.StereoWeight0Q13 = w0;
        state.StereoWeight1Q13 = w1;
    }

    /// <summary>Decodes the subframe gains (RFC 6716 §4.2.7.4, Tables 11 through 13).</summary>
    private void DecodeGains(ref RangeDecoder dec, SilkChannelFrame target, int channel, bool firstOfType, bool previousCoded)
    {
        // RFC 6716 §4.2.7.4: the clamp and delta coding use the gain from the most recent
        // coded subframe in this channel, whichever frame type it belonged to.
        ref int previousLogGain = ref _previousLogGain[channel];
        for (int k = 0; k < _subframes; k++)
        {
            int logGain;
            if (k == 0 && (firstOfType || !previousCoded))
            {
                // Independent coding: 3 MSB then 3 LSB, then the previous-gain clamp.
                int msb = dec.DecodePdf(SilkTables.GainMsbPdf[(int)target.SignalType]);
                int lsb = dec.DecodePdf(SilkTables.GainLsbPdf);
                logGain = (msb << 3) | lsb;
                if (previousLogGain >= 0)
                {
                    logGain = Math.Max(logGain, previousLogGain - 16);
                }
            }
            else
            {
                int previous = previousLogGain >= 0 ? previousLogGain : 0;
                int delta = dec.DecodePdf(SilkTables.GainDeltaPdf);
                logGain = Math.Clamp(
                    Math.Max((2 * delta) - 16, previous + delta - 4), 0, 63);
            }

            target.LogGainIndex[k] = logGain;
            target.GainQ16[k] = DequantizeGainQ16(logGain);
            previousLogGain = logGain;
        }
    }

    private static byte[][] BuildNlsfSelect(string[] source, char baseLetter)
    {
        var result = new byte[source.Length][];
        for (int i = 0; i < source.Length; i++)
        {
            string letters = source[i];
            var row = new byte[letters.Length];
            for (int k = 0; k < letters.Length; k++)
            {
                row[k] = (byte)(letters[k] - baseLetter);
            }

            result[i] = row;
        }

        return result;
    }

    /// <summary>Decodes the normalized LSF indices (RFC 6716 §4.2.7.5.1, §4.2.7.5.2 and §4.2.7.5.5).</summary>
    private void DecodeNlsf(ref RangeDecoder dec, SilkChannelFrame target)
    {
        bool wideband = _bandwidthIndex == 2;
        bool voiced = target.IsVoiced;

        byte[] stage1 = wideband
            ? (voiced ? SilkTables.NlsfStage1WbVoicedPdf : SilkTables.NlsfStage1WbInactivePdf)
            : (voiced ? SilkTables.NlsfStage1NbMbVoicedPdf : SilkTables.NlsfStage1NbMbInactivePdf);
        int i1 = dec.DecodePdf(stage1);
        target.NlsfStage1Index = i1;

        // RFC 6716 §4.2.7.5.1: NB and MB use an order-10 predictor, WB an order-16 one.
        int order = wideband ? 16 : 10;
        ReadOnlySpan<byte> selection = wideband ? NlsfSelectWb[i1] : NlsfSelectNbMb[i1];
        byte[][] pdfs = wideband ? SilkTables.NlsfStage2WbPdf : SilkTables.NlsfStage2NbMbPdf;

        for (int k = 0; k < order; k++)
        {
            // RFC 6716 §4.2.7.5.2: 9 symbols centre on index 0; -4 and 4 escape to Table 19.
            int index = dec.DecodePdf(pdfs[selection[k]]) - 4;
            if (index is -4 or 4)
            {
                int extension = dec.DecodePdf(SilkTables.NlsfExtensionPdf);
                index += index > 0 ? extension : -extension;
            }

            target.NlsfStage2Index[k] = index;
        }

        // RFC 6716 §4.2.7.5.5: the Q2 interpolation factor is coded only for 20 ms frames.
        target.NlsfInterpolationFactor = _subframes == 4
            ? dec.DecodePdf(SilkTables.NlsfInterpolationPdf)
            : 4;
    }

    /// <summary>Decodes the LTP parameters of a voiced frame (RFC 6716 §4.2.7.6, Tables 29 through 42).</summary>
    private void DecodeLtp(
        ref RangeDecoder dec,
        SilkChannelFrame target,
        ref ChannelState state,
        bool isLbrr,
        bool firstOfType,
        bool previousCoded,
        int previousSignalType)
    {
        // RFC 6716 §4.2.7.6.1: absolute coding unless the previous same-type frame in this
        // Opus frame was coded and voiced.
        bool absolute = firstOfType
            || !previousCoded
            || previousSignalType != (int)SilkSignalType.Voiced;

        int lag;
        if (absolute)
        {
            lag = DecodeAbsoluteLag(ref dec);
        }
        else
        {
            int delta = dec.DecodePdf(SilkTables.PitchLagDeltaPdf);
            lag = delta == 0 ? DecodeAbsoluteLag(ref dec) : state.PreviousLag + delta - 9;
        }

        state.PreviousLag = lag;
        target.PrimaryPitchLag = lag;

        int contour = dec.DecodePdf(ContourPdf());
        target.PitchContourIndex = contour;
        ReadOnlySpan<sbyte> offsets = ContourOffsets();
        int lagMin = SilkTables.PitchLagMin[_bandwidthIndex];
        int lagMax = SilkTables.PitchLagMax[_bandwidthIndex];
        int contourOffset = contour * _subframes;
        for (int k = 0; k < _subframes; k++)
        {
            target.PitchLag[k] = Math.Clamp(lag + offsets[contourOffset + k], lagMin, lagMax);
        }

        // RFC 6716 §4.2.7.6.2: one periodicity index chooses the LTP filter codebook.
        int periodicity = dec.DecodePdf(SilkTables.LtpPeriodicityPdf);
        target.LtpPeriodicityIndex = periodicity;
        byte[] filterPdf = SilkTables.LtpFilterPdf[periodicity];
        for (int k = 0; k < _subframes; k++)
        {
            target.LtpFilterIndex[k] = dec.DecodePdf(filterPdf);
        }

        // RFC 6716 §4.2.7.6.3: regular frames code the scaling only in the first interval;
        // LBRR frames also code it when the previous LBRR frame was not coded.
        bool scalingCoded = isLbrr ? (firstOfType || !previousCoded) : firstOfType;
        if (scalingCoded)
        {
            int symbol = dec.DecodePdf(SilkTables.LtpScalingPdf);
            target.LtpScalingQ14 = SilkTables.LtpScalingQ14[symbol];
        }
        else
        {
            target.LtpScalingQ14 = SilkTables.LtpScalingDefaultQ14;
        }
    }

    /// <summary>Decodes an absolute primary pitch lag (RFC 6716 §4.2.7.6.1, Tables 29 and 30).</summary>
    private int DecodeAbsoluteLag(ref RangeDecoder dec)
    {
        int high = dec.DecodePdf(SilkTables.PitchLagHighPdf);
        int low = dec.DecodePdf(SilkTables.PitchLagLowPdf[_bandwidthIndex]);
        return (high * SilkTables.PitchLagScale[_bandwidthIndex])
            + low
            + SilkTables.PitchLagMin[_bandwidthIndex];
    }

    /// <summary>Returns the pitch contour PDF for the current bandwidth and SILK frame size (Table 32).</summary>
    private byte[] ContourPdf()
    {
        bool narrowband = _bandwidthIndex == 0;
        return (narrowband, _subframes) switch
        {
            (true, 2) => SilkTables.PitchContourPdfNb10,
            (true, _) => SilkTables.PitchContourPdfNb20,
            (false, 2) => SilkTables.PitchContourPdfMbWb10,
            _ => SilkTables.PitchContourPdfMbWb20,
        };
    }

    /// <summary>Returns the pitch contour codebook for the current bandwidth and SILK frame size.</summary>
    private ReadOnlySpan<sbyte> ContourOffsets()
    {
        bool narrowband = _bandwidthIndex == 0;
        return (narrowband, _subframes) switch
        {
            (true, 2) => SilkTables.PitchContourNb10,
            (true, _) => SilkTables.PitchContourNb20,
            (false, 2) => SilkTables.PitchContourMbWb10,
            _ => SilkTables.PitchContourMbWb20,
        };
    }

    /// <summary>
    /// Decodes the seed, rate level, pulse counts, pulse locations, LSBs and signs
    /// (RFC 6716 §4.2.7.7 and §4.2.7.8).
    /// </summary>
    private void DecodeExcitation(ref RangeDecoder dec, SilkChannelFrame target)
    {
        // RFC 6716 §4.2.7.7: the 2-bit LCG seed follows the LTP parameters.
        target.Seed = dec.DecodePdf(SilkTables.SeedPdf);

        // RFC 6716 §4.2.7.8.1: the rate level selects the pulse count PDF.
        int rateLevel = dec.DecodePdf(
            target.IsVoiced ? SilkTables.RateLevelVoicedPdf : SilkTables.RateLevelInactivePdf);
        target.RateLevel = rateLevel;

        // RFC 6716 §4.2.7.8, Table 44: shell block count by bandwidth and SILK frame size.
        int blocks = SilkTables.ShellBlockCount[_bandwidthIndex][_subframes == 2 ? 0 : 1];
        target.ShellBlockCount = blocks;

        // RFC 6716 §4.2.7.8.2: all pulse counts precede any block content. 17 escapes to the
        // special rate levels 9 and (after ten escapes) 10, and means "extra LSBs follow".
        for (int block = 0; block < blocks; block++)
        {
            int pulses = dec.DecodePdf(SilkTables.PulseCountPdf[rateLevel]);
            int lsbCount = 0;
            while (pulses == 17)
            {
                lsbCount++;
                pulses = dec.DecodePdf(SilkTables.PulseCountPdf[lsbCount >= 10 ? 10 : 9]);
            }

            target.ShellBlockPulseCount[block] = pulses;
            target.ShellBlockLsbCount[block] = lsbCount;
        }

        // RFC 6716 §4.2.7.8.3: all pulse locations precede the LSBs.
        for (int block = 0; block < blocks; block++)
        {
            DecodeShellBlock(
                ref dec,
                target.ShellBlockPulseCount[block],
                target.Pulses.AsSpan(block * SilkChannelFrame.ShellBlockSize, SilkChannelFrame.ShellBlockSize));
        }

        // RFC 6716 §4.2.7.8.4: all LSBs follow, block by block, most significant bit first.
        for (int block = 0; block < blocks; block++)
        {
            int lsbCount = target.ShellBlockLsbCount[block];
            Span<short> blockPulses = target.Pulses.AsSpan(
                block * SilkChannelFrame.ShellBlockSize, SilkChannelFrame.ShellBlockSize);
            for (int sample = 0; sample < SilkChannelFrame.ShellBlockSize; sample++)
            {
                int magnitude = blockPulses[sample];
                for (int bit = 0; bit < lsbCount; bit++)
                {
                    magnitude = (magnitude << 1) | dec.DecodePdf(SilkTables.LsbPdf);
                }

                blockPulses[sample] = (short)magnitude;
            }
        }

        // RFC 6716 §4.2.7.8.5: signs last, for every non-zero magnitude. A zero symbol negates.
        byte[][] signPdfs = SilkTables.SignPdf[(int)target.SignalType][target.QuantizationOffsetType];
        for (int block = 0; block < blocks; block++)
        {
            byte[] signPdf = signPdfs[Math.Min(target.ShellBlockPulseCount[block], 6)];
            Span<short> blockPulses = target.Pulses.AsSpan(
                block * SilkChannelFrame.ShellBlockSize, SilkChannelFrame.ShellBlockSize);
            for (int sample = 0; sample < SilkChannelFrame.ShellBlockSize; sample++)
            {
                if (blockPulses[sample] != 0 && dec.DecodePdf(signPdf) == 0)
                {
                    blockPulses[sample] = (short)-blockPulses[sample];
                }
            }
        }
    }

    /// <summary>Recursively splits a partition's pulses (RFC 6716 §4.2.7.8.3).</summary>
    private static void SplitPulses(ref RangeDecoder dec, Span<short> pulses, int offset, int size, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (size == 2)
        {
            int split = dec.DecodePdf(SilkTables.PulseSplitPdf(2, count));
            pulses[offset] = (short)split;
            pulses[offset + 1] = (short)(count - split);
            return;
        }

        int left = dec.DecodePdf(SilkTables.PulseSplitPdf(size, count));
        SplitPulses(ref dec, pulses, offset, size / 2, left);
        SplitPulses(ref dec, pulses, offset + (size / 2), size / 2, count - left);
    }
}
