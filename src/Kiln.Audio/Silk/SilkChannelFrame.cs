namespace Kiln.Audio.Silk;

/// <summary>Signal type of a SILK frame (RFC 6716 §4.2.7.3, Table 10).</summary>
internal enum SilkSignalType
{
    /// <summary>Inactive speech.</summary>
    Inactive = 0,

    /// <summary>Unvoiced speech.</summary>
    Unvoiced = 1,

    /// <summary>Voiced speech, the only type that carries LTP parameters.</summary>
    Voiced = 2,
}

/// <summary>The decoded parameters of one SILK frame in one channel.</summary>
/// <remarks>Plain fields on a reusable object keep the decode path allocation-free.</remarks>
internal sealed class SilkChannelFrame
{
    /// <summary>Maximum LPC order, which is also the longest NLSF index vector (WB).</summary>
    internal const int MaxLpcOrder = 16;

    /// <summary>Maximum number of 5 ms subframes in a SILK frame.</summary>
    internal const int MaxSubframes = 4;

    /// <summary>Maximum number of 16-sample shell blocks in a SILK frame (20 ms WB).</summary>
    internal const int MaxShellBlocks = 20;

    /// <summary>Samples per shell block (RFC 6716 §4.2.7.8).</summary>
    internal const int ShellBlockSize = 16;

    /// <summary>Maximum number of excitation samples in one SILK frame.</summary>
    internal const int MaxSamples = MaxShellBlocks * ShellBlockSize;

    /// <summary>Gets or sets the signal type (RFC 6716 §4.2.7.3).</summary>
    internal SilkSignalType SignalType { get; set; }

    /// <summary>Gets or sets the quantization offset type: 0 for low, 1 for high (RFC 6716 §4.2.7.3).</summary>
    internal int QuantizationOffsetType { get; set; }

    /// <summary>Gets a value indicating whether the frame is voiced and carries LTP parameters.</summary>
    internal bool IsVoiced => SignalType == SilkSignalType.Voiced;

    /// <summary>Gets or sets the LCG seed, 0 to 3 (RFC 6716 §4.2.7.7).</summary>
    internal int Seed { get; set; }

    /// <summary>Gets or sets the NLSF stage-1 index, 0 to 31 (RFC 6716 §4.2.7.5.1).</summary>
    internal int NlsfStage1Index { get; set; }

    /// <summary>Gets the NLSF stage-2 residual indices, -10 to 10 (RFC 6716 §4.2.7.5.2).</summary>
    internal int[] NlsfStage2Index { get; } = new int[MaxLpcOrder];

    /// <summary>Gets or sets the Q2 NLSF interpolation factor, or 4 for 10 ms frames (RFC 6716 §4.2.7.5.5).</summary>
    internal int NlsfInterpolationFactor { get; set; }

    /// <summary>Gets the decoded quantization gain index per subframe, 0 to 63 (RFC 6716 §4.2.7.4).</summary>
    internal int[] LogGainIndex { get; } = new int[MaxSubframes];

    /// <summary>Gets the dequantized quantization gain per subframe, Q16 (RFC 6716 §4.2.7.4).</summary>
    internal int[] GainQ16 { get; } = new int[MaxSubframes];

    /// <summary>Gets the primary pitch lag in samples, before contour offsets (RFC 6716 §4.2.7.6.1).</summary>
    internal int PrimaryPitchLag { get; set; }

    /// <summary>Gets the final pitch lag per subframe, clamped to the bandwidth's lag range (RFC 6716 §4.2.7.6.1).</summary>
    internal int[] PitchLag { get; } = new int[MaxSubframes];

    /// <summary>Gets or sets the pitch contour codebook index (RFC 6716 §4.2.7.6.1).</summary>
    internal int PitchContourIndex { get; set; }

    /// <summary>Gets or sets the LTP periodicity index, 0 to 2 (RFC 6716 §4.2.7.6.2).</summary>
    internal int LtpPeriodicityIndex { get; set; }

    /// <summary>Gets the LTP filter codebook index per subframe (RFC 6716 §4.2.7.6.2).</summary>
    internal int[] LtpFilterIndex { get; } = new int[MaxSubframes];

    /// <summary>Gets or sets the LTP scaling factor, Q14 (RFC 6716 §4.2.7.6.3).</summary>
    internal int LtpScalingQ14 { get; set; }

    /// <summary>Gets or sets the excitation rate level, 0 to 8 (RFC 6716 §4.2.7.8.1).</summary>
    internal int RateLevel { get; set; }

    /// <summary>Gets or sets the number of shell blocks decoded for the excitation (RFC 6716 §4.2.7.8, Table 44).</summary>
    internal int ShellBlockCount { get; set; }

    /// <summary>Gets the number of pulses in each shell block, not counting LSBs (RFC 6716 §4.2.7.8.2).</summary>
    internal int[] ShellBlockPulseCount { get; } = new int[MaxShellBlocks];

    /// <summary>Gets the number of extra LSBs decoded for each shell block (RFC 6716 §4.2.7.8.2).</summary>
    internal int[] ShellBlockLsbCount { get; } = new int[MaxShellBlocks];

    /// <summary>
    /// Gets the signed excitation magnitudes after pulse locations, LSBs and signs
    /// (RFC 6716 §4.2.7.8.3 to §4.2.7.8.5). Only the first
    /// <see cref="ShellBlockCount"/> * 16 entries are meaningful.
    /// </summary>
    internal short[] Pulses { get; } = new short[MaxSamples];

    /// <summary>Gets or sets the first stereo prediction weight, Q13 (RFC 6716 §4.2.7.1).</summary>
    internal int StereoWeight0Q13 { get; set; }

    /// <summary>Gets or sets the second stereo prediction weight, Q13 (RFC 6716 §4.2.7.1).</summary>
    internal int StereoWeight1Q13 { get; set; }

    /// <summary>Gets or sets the mid-only flag, when the frame is a mid channel (RFC 6716 §4.2.7.2).</summary>
    internal bool IsMidOnly { get; set; }

    /// <summary>Returns the frame to the state of a freshly decoded, all-zero frame.</summary>
    internal void Clear()
    {
        SignalType = SilkSignalType.Inactive;
        QuantizationOffsetType = 0;
        Seed = 0;
        NlsfStage1Index = 0;
        NlsfStage2Index.AsSpan().Clear();
        NlsfInterpolationFactor = 4;
        LogGainIndex.AsSpan().Clear();
        GainQ16.AsSpan().Clear();
        PrimaryPitchLag = 0;
        PitchLag.AsSpan().Clear();
        PitchContourIndex = 0;
        LtpPeriodicityIndex = 0;
        LtpFilterIndex.AsSpan().Clear();
        LtpScalingQ14 = SilkTables.LtpScalingDefaultQ14;
        RateLevel = 0;
        ShellBlockCount = 0;
        ShellBlockPulseCount.AsSpan().Clear();
        ShellBlockLsbCount.AsSpan().Clear();
        Pulses.AsSpan().Clear();
        StereoWeight0Q13 = 0;
        StereoWeight1Q13 = 0;
        IsMidOnly = false;
    }
}
