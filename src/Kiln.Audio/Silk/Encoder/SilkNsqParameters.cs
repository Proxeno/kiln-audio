// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk;

/// <summary>
/// The per-frame inputs of the SILK noise shaping quantizer (RFC 6716 §5.2.3.8,
/// Appendix A parameters of <c>silk_NSQ_del_dec</c>).
/// </summary>
/// <remarks>
/// All arrays are caller-owned and length-checked by
/// <see cref="SilkNsqDelayedDecision.Run"/>. The type is a <see langword="ref struct"/> so the
/// quantizer can take it without copying.
/// </remarks>
internal readonly ref struct SilkNsqParameters
{
    /// <summary>Creates the parameter block.</summary>
    public SilkNsqParameters(
        ReadOnlySpan<int> xQ3,
        ReadOnlySpan<short> predictionCoefficientsQ12,
        ReadOnlySpan<short> ltpCoefficientsQ14,
        ReadOnlySpan<short> shapingCoefficientsQ13,
        ReadOnlySpan<int> harmonicShapeGainQ14,
        ReadOnlySpan<int> tiltQ14,
        ReadOnlySpan<int> lowFrequencyShapingQ14,
        ReadOnlySpan<int> gainsQ16,
        ReadOnlySpan<int> pitchLags,
        int signalType,
        int quantizationOffsetType,
        int lambdaQ10,
        int ltpScaleQ14,
        int seed,
        int nlsfInterpolationFactor,
        int subframeCount,
        int subframeLength,
        int ltpMemoryLength,
        int predictLpcOrder,
        int shapingLpcOrder,
        int warpingQ16)
    {
        XQ3 = xQ3;
        PredictionCoefficientsQ12 = predictionCoefficientsQ12;
        LtpCoefficientsQ14 = ltpCoefficientsQ14;
        ShapingCoefficientsQ13 = shapingCoefficientsQ13;
        HarmonicShapeGainQ14 = harmonicShapeGainQ14;
        TiltQ14 = tiltQ14;
        LowFrequencyShapingQ14 = lowFrequencyShapingQ14;
        GainsQ16 = gainsQ16;
        PitchLags = pitchLags;
        SignalType = signalType;
        QuantizationOffsetType = quantizationOffsetType;
        LambdaQ10 = lambdaQ10;
        LtpScaleQ14 = ltpScaleQ14;
        Seed = seed;
        NlsfInterpolationFactor = nlsfInterpolationFactor;
        SubframeCount = subframeCount;
        SubframeLength = subframeLength;
        LtpMemoryLength = ltpMemoryLength;
        PredictLpcOrder = predictLpcOrder;
        ShapingLpcOrder = shapingLpcOrder;
        WarpingQ16 = warpingQ16;
    }

    /// <summary>Prefiltered input signal, Q3.</summary>
    public ReadOnlySpan<int> XQ3 { get; }

    /// <summary>Two short-term prediction coefficient sets, Q12 (RFC 6716 §4.2.7.5.5).</summary>
    public ReadOnlySpan<short> PredictionCoefficientsQ12 { get; }

    /// <summary>Long-term prediction coefficients, Q14, <c>LTP_ORDER * SubframeCount</c> entries.</summary>
    public ReadOnlySpan<short> LtpCoefficientsQ14 { get; }

    /// <summary>Noise shaping coefficients, Q13, <c>MaxShapeLpcOrder * SubframeCount</c> entries.</summary>
    public ReadOnlySpan<short> ShapingCoefficientsQ13 { get; }

    /// <summary>Harmonic shaping gains, Q14.</summary>
    public ReadOnlySpan<int> HarmonicShapeGainQ14 { get; }

    /// <summary>Spectral tilt, Q14.</summary>
    public ReadOnlySpan<int> TiltQ14 { get; }

    /// <summary>Low-frequency shaping coefficients, Q14.</summary>
    public ReadOnlySpan<int> LowFrequencyShapingQ14 { get; }

    /// <summary>Quantization gains, Q16.</summary>
    public ReadOnlySpan<int> GainsQ16 { get; }

    /// <summary>Pitch lag per subframe, in samples.</summary>
    public ReadOnlySpan<int> PitchLags { get; }

    /// <summary>The frame's signal type: 0 inactive, 1 unvoiced, 2 voiced.</summary>
    public int SignalType { get; }

    /// <summary>The quantization offset type: 0 low, 1 high.</summary>
    public int QuantizationOffsetType { get; }

    /// <summary>The rate/distortion tradeoff, Q10.</summary>
    public int LambdaQ10 { get; }

    /// <summary>The LTP state scaling, Q14.</summary>
    public int LtpScaleQ14 { get; }

    /// <summary>The LCG seed, 0 to 3.</summary>
    public int Seed { get; }

    /// <summary>The NLSF interpolation factor, Q2; 4 disables interpolation.</summary>
    public int NlsfInterpolationFactor { get; }

    /// <summary>The number of 5 ms subframes, 2 or 4.</summary>
    public int SubframeCount { get; }

    /// <summary>The number of samples in a subframe.</summary>
    public int SubframeLength { get; }

    /// <summary>The length of the long-term prediction history.</summary>
    public int LtpMemoryLength { get; }

    /// <summary>The short-term prediction filter order, 10 or 16.</summary>
    public int PredictLpcOrder { get; }

    /// <summary>The noise shaping filter order, even and at most 16.</summary>
    public int ShapingLpcOrder { get; }

    /// <summary>The warping parameter, Q16 (zero when warping is off).</summary>
    public int WarpingQ16 { get; }
}
