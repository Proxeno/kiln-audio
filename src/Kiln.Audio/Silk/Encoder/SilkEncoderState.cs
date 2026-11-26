// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>
/// The noise shaping quantizer state (RFC 6716 Appendix A, <c>silk_nsq_state</c>).
/// </summary>
/// <remarks>
/// After construction the encoder mutates this object in place and copies it with
/// <see cref="CopyFrom"/> for the bitrate loop; nothing here allocates per frame.
/// </remarks>
internal sealed class SilkNsqState
{
    internal readonly short[] Xq = new short[2 * SilkEncoderMath.MaxFrameLength];
    internal readonly int[] SLtpShpQ14 = new int[2 * SilkEncoderMath.MaxFrameLength];
    // NSQ_LPC_BUF_LENGTH is 32 in the reference (DECISION_DELAY exceeds the LPC order).
    internal readonly int[] SLpcQ14 = new int[SilkEncoderMath.MaxSubFrameLength + 32];
    internal readonly int[] SAr2Q14 = new int[SilkEncoderMath.MaxShapeLpcOrder];
    internal int SLfArShpQ14;
    internal int LagPrev;
    internal int SLtpBufIdx;
    internal int SLtpShpBufIdx;
    internal int RandSeed;
    internal int PrevGainQ16;
    internal int RewhiteFlag;

    internal void Reset()
    {
        Xq.AsSpan().Clear();
        SLtpShpQ14.AsSpan().Clear();
        SLpcQ14.AsSpan().Clear();
        SAr2Q14.AsSpan().Clear();
        SLfArShpQ14 = 0;
        LagPrev = 0;
        SLtpBufIdx = 0;
        SLtpShpBufIdx = 0;
        RandSeed = 0;
        PrevGainQ16 = 0;
        RewhiteFlag = 0;
    }

    internal void CopyFrom(SilkNsqState other)
    {
        other.Xq.AsSpan().CopyTo(Xq);
        other.SLtpShpQ14.AsSpan().CopyTo(SLtpShpQ14);
        other.SLpcQ14.AsSpan().CopyTo(SLpcQ14);
        other.SAr2Q14.AsSpan().CopyTo(SAr2Q14);
        SLfArShpQ14 = other.SLfArShpQ14;
        LagPrev = other.LagPrev;
        SLtpBufIdx = other.SLtpBufIdx;
        SLtpShpBufIdx = other.SLtpShpBufIdx;
        RandSeed = other.RandSeed;
        PrevGainQ16 = other.PrevGainQ16;
        RewhiteFlag = other.RewhiteFlag;
    }
}

/// <summary>The noise shaping analysis state (RFC 6716 Appendix A, <c>silk_shape_state_FIX</c>).</summary>
internal sealed class SilkShapeState
{
    internal int LastGainIndex;
    internal int HarmBoostSmthQ16;
    internal int HarmShapeGainSmthQ16;
    internal int TiltSmthQ16;

    internal void Reset()
    {
        LastGainIndex = 0;
        HarmBoostSmthQ16 = 0;
        HarmShapeGainSmthQ16 = 0;
        TiltSmthQ16 = 0;
    }
}

/// <summary>The prefilter state (RFC 6716 Appendix A, <c>silk_prefilter_state_FIX</c>).</summary>
internal sealed class SilkPrefilterState
{
    internal readonly short[] SLtpShp = new short[SilkEncoderMath.LtbBufLength];
    internal readonly int[] SArShp = new int[SilkEncoderMath.MaxShapeLpcOrder + 1];
    internal int SLtpShpBufIdx;
    internal int SLfArShpQ12;
    internal int SLfMaShpQ12;
    internal int SHarmHpQ2;
    internal int RandSeed;
    internal int LagPrev;

    internal void Reset()
    {
        SLtpShp.AsSpan().Clear();
        SArShp.AsSpan().Clear();
        SLtpShpBufIdx = 0;
        SLfArShpQ12 = 0;
        SLfMaShpQ12 = 0;
        SHarmHpQ2 = 0;
        RandSeed = 0;
        LagPrev = 0;
    }
}

/// <summary>The voice activity detector state (RFC 6716 Appendix A, <c>silk_VAD_state</c>).</summary>
internal sealed class SilkVadState
{
    internal readonly int[] AnaState = new int[2];
    internal readonly int[] AnaState1 = new int[2];
    internal readonly int[] AnaState2 = new int[2];
    internal readonly int[] XnrgSubfr = new int[SilkEncoderMath.VadBandCount];
    internal readonly int[] NrgRatioSmthQ8 = new int[SilkEncoderMath.VadBandCount];
    internal int HpState;
    internal readonly int[] Nl = new int[SilkEncoderMath.VadBandCount];
    internal readonly int[] InvNl = new int[SilkEncoderMath.VadBandCount];
    internal readonly int[] NoiseLevelBias = new int[SilkEncoderMath.VadBandCount];
    internal int Counter;

    internal void Reset()
    {
        AnaState.AsSpan().Clear();
        AnaState1.AsSpan().Clear();
        AnaState2.AsSpan().Clear();
        XnrgSubfr.AsSpan().Clear();
        NrgRatioSmthQ8.AsSpan().Clear();
        HpState = 0;
        Nl.AsSpan().Clear();
        InvNl.AsSpan().Clear();
        NoiseLevelBias.AsSpan().Clear();
        Counter = 0;
    }
}

/// <summary>The variable-cutoff low-pass state (RFC 6716 Appendix A, <c>silk_LP_state</c>).</summary>
internal sealed class SilkLpState
{
    internal readonly int[] InLpState = new int[2];
    internal int TransitionFrameNo;
    internal int Mode;

    internal void Reset()
    {
        InLpState.AsSpan().Clear();
        TransitionFrameNo = 0;
        Mode = 0;
    }
}

/// <summary>The quantization indices of one SILK frame (RFC 6716 Appendix A, <c>SideInfoIndices</c>).</summary>
internal sealed class SilkSideInfo
{
    internal readonly int[] GainsIndices = new int[SilkEncoderMath.MaxFrameLength / SilkEncoderMath.MaxSubFrameLength];
    internal readonly int[] LtpIndex = new int[SilkEncoderMath.MaxFrameLength / SilkEncoderMath.MaxSubFrameLength];
    internal readonly int[] NlsfIndices = new int[SilkEncoderMath.MaxLpcOrder + 1];
    internal int LagIndex;
    internal int ContourIndex;
    internal int SignalType;
    internal int QuantOffsetType;
    internal int NlsfInterpCoefQ2;
    internal int PerIndex;
    internal int LtpScaleIndex;
    internal int Seed;

    internal void Clear()
    {
        GainsIndices.AsSpan().Clear();
        LtpIndex.AsSpan().Clear();
        NlsfIndices.AsSpan().Clear();
        LagIndex = 0;
        ContourIndex = 0;
        SignalType = 0;
        QuantOffsetType = 0;
        NlsfInterpCoefQ2 = 4;
        PerIndex = 0;
        LtpScaleIndex = 0;
        Seed = 0;
    }

    internal void CopyFrom(SilkSideInfo other)
    {
        other.GainsIndices.AsSpan().CopyTo(GainsIndices);
        other.LtpIndex.AsSpan().CopyTo(LtpIndex);
        other.NlsfIndices.AsSpan().CopyTo(NlsfIndices);
        LagIndex = other.LagIndex;
        ContourIndex = other.ContourIndex;
        SignalType = other.SignalType;
        QuantOffsetType = other.QuantOffsetType;
        NlsfInterpCoefQ2 = other.NlsfInterpCoefQ2;
        PerIndex = other.PerIndex;
        LtpScaleIndex = other.LtpScaleIndex;
        Seed = other.Seed;
    }
}

/// <summary>
/// The per-frame analysis output (RFC 6716 Appendix A, <c>silk_encoder_control_FIX</c>).
/// </summary>
internal sealed class SilkEncoderControl
{
    internal readonly int[] GainsQ16 = new int[4];
    internal readonly short[][] PredCoefQ12 = [new short[SilkEncoderMath.MaxLpcOrder], new short[SilkEncoderMath.MaxLpcOrder]];
    internal readonly short[] LtpCoefQ14 = new short[SilkEncoderMath.LtpOrder * 4];
    internal int LtpScaleQ14;
    internal readonly int[] PitchL = new int[4];
    internal readonly short[] Ar1Q13 = new short[4 * SilkEncoderMath.MaxShapeLpcOrder];
    internal readonly short[] Ar2Q13 = new short[4 * SilkEncoderMath.MaxShapeLpcOrder];
    internal readonly int[] LfShpQ14 = new int[4];
    internal readonly int[] GainsPreQ14 = new int[4];
    internal readonly int[] HarmBoostQ14 = new int[4];
    internal readonly int[] TiltQ14 = new int[4];
    internal readonly int[] HarmShapeGainQ14 = new int[4];
    internal int LambdaQ10;
    internal int InputQualityQ14;
    internal int CodingQualityQ14;
    internal int SparsenessQ8;
    internal int PredGainQ16;
    internal int LtPredCodGainQ7;
    internal readonly int[] ResNrg = new int[4];
    internal readonly int[] ResNrgQ = new int[4];
    internal readonly int[] GainsUnqQ16 = new int[4];
    internal int LastGainIndexPrev;
}

/// <summary>
/// The complete fixed-point SILK encoder state for one channel (RFC 6716 Appendix A,
/// <c>silk_encoder_state_FIX</c> plus the common <c>silk_encoder_state</c>).
/// </summary>
/// <remarks>
/// Only the mono, SILK-only path is implemented here, so the two-channel outer structure and the
/// stereo mid/side processing are omitted. Every array is allocated once; the per-frame encode
/// path only mutates existing storage.
/// </remarks>
internal sealed class SilkEncoderState
{
    /// <summary>Maximum SILK frames in one Opus frame.</summary>
    internal const int MaxFramesPerPacket = 3;

    internal readonly int[] InHpState = new int[2];
    internal int VariableHpSmth1Q15;
    internal int VariableHpSmth2Q15;
    internal readonly SilkLpState Lp = new();
    internal readonly SilkVadState Vad = new();
    internal readonly SilkNsqState Nsq = new();
    internal readonly short[] PrevNlsfQ15 = new short[SilkEncoderMath.MaxLpcOrder];
    internal int SpeechActivityQ8;
    internal int PrevSignalType;
    internal int PrevLag;
    internal int PitchLpcWinLength;
    internal int MaxPitchLag;
    internal int ApiFsHz;
    internal int PrevApiFsHz;
    internal int MaxInternalFsHz;
    internal int MinInternalFsHz;
    internal int DesiredInternalFsHz;
    internal int FsKhz;
    internal int SubframeCount;
    internal int FrameLength;
    internal int SubframeLength;
    internal int LtpMemLength;
    internal int LaPitch;
    internal int LaShape;
    internal int ShapeWinLength;
    internal int TargetRateBps;
    internal int PacketSizeMs;
    internal int FrameCounter;
    internal int Complexity;
    internal int NStatesDelayedDecision;
    internal int UseInterpolatedNlsfs;
    internal int ShapingLpcOrder;
    internal int PredictLpcOrder;
    internal int PitchEstimationComplexity;
    internal int PitchEstimationLpcOrder;
    internal int PitchEstimationThresholdQ16;
    internal int LtpQuantLowComplexity;
    internal int MuLtpQ9;
    internal int NlsfMsvqSurvivors;
    internal int FirstFrameAfterReset;
    internal int WarpingQ16;
    internal int UseCbr;
    internal int InputTiltQ15;
    internal readonly int[] InputQualityBandsQ15 = new int[SilkEncoderMath.VadBandCount];
    internal int SnrDbQ7;
    internal int FramesSinceOnset;

    internal readonly int[] VadFlags = new int[MaxFramesPerPacket];
    internal int LbrrFlag;
    internal readonly int[] LbrrFlags = new int[MaxFramesPerPacket];

    internal SilkSideInfo Indices = new();
    internal readonly short[] Pulses = new short[SilkEncoderMath.MaxFrameLength];

    // Low-bitrate redundancy (LBRR) frames for the next packet (RFC 6716 §5.2.3, silk_LBRR_encode_FIX).
    internal readonly SilkSideInfo[] LbrrIndices = [new(), new(), new()];
    internal readonly short[][] LbrrPulses =
    [
        new short[SilkEncoderMath.MaxFrameLength],
        new short[SilkEncoderMath.MaxFrameLength],
        new short[SilkEncoderMath.MaxFrameLength],
    ];
    internal int LbrrEnabled;
    internal int LbrrGainIncreases;
    internal int LbrrPrevLastGainIndex;

    internal readonly short[] InputBuf = new short[SilkEncoderMath.MaxFrameLength + 2];
    internal int InputBufIx;
    internal int FramesPerPacket;
    internal int FramesEncoded;

    internal int EcPrevSignalType;
    internal int EcPrevLagIndex;

    internal int UseDtx;
    internal int InDtx;
    internal int NoSpeechCounter;

    internal readonly short[] XBuf = new short[
        (2 * SilkEncoderMath.MaxFrameLength) + SilkEncoderMath.LaShapeMax];
    internal readonly SilkShapeState Shape = new();
    internal readonly SilkPrefilterState Prefilt = new();
    internal int LtpCorrQ15;

    /// <summary>Resets the parts of the state that a bandwidth change clears.</summary>
    internal void ResetForBandwidthChange()
    {
        Shape.Reset();
        Prefilt.Reset();
        Nsq.Reset();
        PrevNlsfQ15.AsSpan().Clear();
        Lp.InLpState.AsSpan().Clear();
        InputBufIx = 0;
        FramesEncoded = 0;
        TargetRateBps = 0;
        PrevLag = 100;
        FirstFrameAfterReset = 1;
        Prefilt.LagPrev = 100;
        Shape.LastGainIndex = 10;
        Nsq.LagPrev = 100;
        Nsq.PrevGainQ16 = 65536;
        PrevSignalType = 0;
    }
}
