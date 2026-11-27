// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System;

namespace Kiln.Audio.Silk.Encoder;

/// <summary>The NLSF codebook description the encoder needs (RFC 6716 Appendix A, <c>silk_NLSF_CB_struct</c>).</summary>
/// <remarks>
/// The codebook vectors, the backward predictor weights, the stage-2 PDF selection and the
/// minimum spacings are shared with the decoder's tables. Only the encoder-side rate estimates
/// are new, because the decoder never needs them.
/// </remarks>
internal sealed class SilkNlsfCodebook
{
    internal required int Order { get; init; }

    internal required int QuantStepSizeQ16 { get; init; }

    internal required int InvQuantStepSizeQ6 { get; init; }

    internal required byte[] Cb1 { get; init; }

    internal required ReadOnlyMemory<byte> PredQ8 { get; init; }

    internal required string[] PredSelect { get; init; }

    internal required string[] Stage2Select { get; init; }

    internal required byte[] Stage2RatesQ5 { get; init; }

    /// <summary>Stage-1 forward PDF for a signal type (0 inactive, 1 unvoiced, 2 voiced).</summary>
    internal required byte[] Stage1InactivePdf { get; init; }

    internal required byte[] Stage1VoicedPdf { get; init; }

    internal ReadOnlySpan<byte> Stage1Pdf(int signalType) =>
        signalType == 2 ? Stage1VoicedPdf : Stage1InactivePdf;
}

/// <summary>
/// The encoder-only constant tables (RFC 6716 Appendix A, <c>tables_*.c</c> and
/// <c>pitch_est_tables.c</c>).
/// </summary>
internal static class SilkEncoderTables
{
    /// <summary>NB/MB NLSF codebook.</summary>
    internal static readonly SilkNlsfCodebook NlsfNbMb = new()
    {
        Order = 10,
        QuantStepSizeQ16 = 11796,  // round(0.18 * 2^16)
        InvQuantStepSizeQ6 = 356,  // round((1 / 0.18) * 2^6)
        Cb1 = SilkSynthesisTables.NlsfCb1NbMbQ8,
        PredQ8 = SilkSynthesisTables.NlsfPredNbMbQ8,
        PredSelect = SilkSynthesisTables.NlsfPredSelectNbMb,
        Stage2Select = SilkTables.NlsfStage2SelectNbMb,
        Stage2RatesQ5 =
        [
            255, 255, 255, 131, 6, 145, 255, 255, 255,
            255, 255, 236, 93, 15, 96, 255, 255, 255,
            255, 255, 194, 83, 25, 71, 221, 255, 255,
            255, 255, 162, 73, 34, 66, 162, 255, 255,
            255, 210, 126, 73, 43, 57, 173, 255, 255,
            255, 201, 125, 71, 48, 58, 130, 255, 255,
            255, 166, 110, 73, 57, 62, 104, 210, 255,
            255, 251, 123, 65, 55, 68, 100, 171, 255,
        ],
        Stage1InactivePdf = SilkTables.NlsfStage1NbMbInactivePdf,
        Stage1VoicedPdf = SilkTables.NlsfStage1NbMbVoicedPdf,
    };

    /// <summary>WB NLSF codebook.</summary>
    internal static readonly SilkNlsfCodebook NlsfWb = new()
    {
        Order = 16,
        QuantStepSizeQ16 = 9830,  // round(0.15 * 2^16)
        InvQuantStepSizeQ6 = 427, // round((1 / 0.15) * 2^6)
        Cb1 = SilkSynthesisTables.NlsfCb1WbQ8,
        PredQ8 = SilkSynthesisTables.NlsfPredWbQ8,
        PredSelect = SilkSynthesisTables.NlsfPredSelectWb,
        Stage2Select = SilkTables.NlsfStage2SelectWb,
        Stage2RatesQ5 =
        [
            255, 255, 255, 156, 4, 154, 255, 255, 255,
            255, 255, 227, 102, 15, 92, 255, 255, 255,
            255, 255, 213, 83, 24, 72, 236, 255, 255,
            255, 255, 150, 76, 33, 63, 214, 255, 255,
            255, 190, 121, 77, 43, 55, 185, 255, 255,
            255, 245, 137, 71, 43, 59, 139, 255, 255,
            255, 255, 131, 66, 50, 66, 107, 194, 255,
            255, 166, 116, 76, 55, 53, 125, 255, 255,
        ],
        Stage1InactivePdf = SilkTables.NlsfStage1WbInactivePdf,
        Stage1VoicedPdf = SilkTables.NlsfStage1WbVoicedPdf,
    };

    // ---------------------------------------------------------------------------------------
    // LTP quantization (tables_LTP.c). The codebooks are shared with the decoder's synthesis
    // tables; the rate estimates and the codebook sizes are encoder-only.
    // ---------------------------------------------------------------------------------------

    /// <summary>Per-codebook bit estimates, Q5, for the LTP gain quantizer (tables_LTP.c).</summary>
    internal static readonly byte[][] LtpGainBitsQ5 =
    [
        [15, 131, 138, 138, 155, 155, 173, 173],
        [
            69, 93, 115, 118, 131, 138, 141, 138,
            150, 150, 155, 150, 155, 160, 166, 160,
        ],
        [
            131, 128, 134, 141, 141, 141, 145, 145,
            145, 150, 155, 155, 155, 155, 160, 160,
            160, 160, 166, 166, 173, 173, 182, 192,
            182, 192, 192, 192, 205, 192, 205, 224,
        ],
    ];

    /// <summary>Number of codebook vectors per periodicity index.</summary>
    internal static readonly int[] LtpCodebookSizes = [8, 16, 32];

    /// <summary>Average rate-distortion of the middle codebook, Q14 (tables_LTP.c).</summary>
    internal const int LtpGainMiddleAverageRdQ14 = 12304;

    // ---------------------------------------------------------------------------------------
    // Pitch estimation codebooks (pitch_est_tables.c).
    // ---------------------------------------------------------------------------------------

    /// <summary>Stage-2 lag codebook for 10 ms frames.</summary>
    internal static readonly sbyte[][] PitchLagStage2_10Ms =
    [
        [0, 1, 0],
        [0, 0, 1],
    ];

    /// <summary>Stage-3 lag codebook for 10 ms frames.</summary>
    internal static readonly sbyte[][] PitchLagStage3_10Ms =
    [
        [0, 0, 1, -1, 1, -1, 2, -2, 2, -2, 3, -3],
        [0, 1, 0, 1, -1, 2, -1, 2, -2, 3, -2, 3],
    ];

    /// <summary>Stage-3 lag search range for 10 ms frames.</summary>
    internal static readonly sbyte[][] PitchLagRangeStage3_10Ms =
    [
        [-3, 7],
        [-2, 7],
    ];

    /// <summary>Stage-2 lag codebook for 20 ms frames.</summary>
    internal static readonly sbyte[][] PitchLagStage2 =
    [
        [0, 2, -1, -1, -1, 0, 0, 1, 1, 0, 1],
        [0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0],
        [0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0],
        [0, -1, 2, 1, 0, 1, 1, 0, 0, -1, -1],
    ];

    /// <summary>Stage-3 lag codebook for 20 ms frames.</summary>
    internal static readonly sbyte[][] PitchLagStage3 =
    [
        [0, 0, 1, -1, 0, 1, -1, 0, -1, 1, -2, 2, -2, -2, 2, -3, 2, 3, -3, -4, 3, -4, 4, 4, -5, 5, -6, -5, 6, -7, 6, 5, 8, -9],
        [0, 0, 1, 0, 0, 0, 0, 0, 0, 0, -1, 1, 0, 0, 1, -1, 0, 1, -1, -1, 1, -1, 2, 1, -1, 2, -2, -2, 2, -2, 2, 2, 3, -3],
        [0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, -1, 1, 0, 0, 2, 1, -1, 2, -1, -1, 2, -1, 2, 2, -1, 3, -2, -2, -2, 3],
        [0, 1, 0, 0, 1, 0, 1, -1, 2, -1, 2, -1, 2, 3, -2, 3, -2, -2, 4, 4, -3, 5, -3, -4, 6, -4, 6, 5, -5, 8, -6, -5, -7, 9],
    ];

    /// <summary>Stage-3 lag search range for 20 ms frames, by complexity.</summary>
    internal static readonly sbyte[][][] PitchLagRangeStage3 =
    [
        [[-5, 8], [-1, 6], [-1, 6], [-4, 10]],
        [[-6, 10], [-2, 6], [-1, 6], [-5, 10]],
        [[-9, 12], [-3, 7], [-2, 7], [-7, 13]],
    ];

    /// <summary>Number of stage-3 lag candidates by complexity.</summary>
    internal static readonly sbyte[] PitchStage3Candidates = [16, 24, 34];

    /// <summary>Pitch estimation complexity names (pitch_est_defines.h).</summary>
    internal const int PitchEstimationMinComplexity = 0;

    /// <summary>Medium pitch estimation complexity.</summary>
    internal const int PitchEstimationMidComplexity = 1;

    /// <summary>Maximum pitch estimation complexity.</summary>
    internal const int PitchEstimationMaxComplexity = 2;

    // ---------------------------------------------------------------------------------------
    // Rate-to-quality mapping (tables_other.c).
    // ---------------------------------------------------------------------------------------

    /// <summary>NB target-rate breakpoints (bits per second).</summary>
    internal static readonly int[] TargetRateTableNb = [0, 8000, 9400, 11500, 13500, 17500, 25000, 80000];

    /// <summary>MB target-rate breakpoints.</summary>
    internal static readonly int[] TargetRateTableMb = [0, 9000, 12000, 14500, 18500, 24500, 35500, 80000];

    /// <summary>WB target-rate breakpoints.</summary>
    internal static readonly int[] TargetRateTableWb = [0, 10500, 14000, 17000, 21500, 28500, 42000, 80000];

    /// <summary>Quality in Q1 dB matching the target-rate breakpoints.</summary>
    internal static readonly short[] SnrTableQ1 = [18, 29, 38, 40, 46, 52, 62, 84];

    /// <summary>Coefficients of the gain quantizer's log-domain mapping (gain_quant.c).</summary>
    internal const int GainOffsetQ7 = (2 * 128 / 6) + (16 * 128);

    /// <summary>Number of Q16 units per gain quantization step.</summary>
    internal const int GainScaleQ16 = (65536 * 63) / (((88 - 2) * 128) / 6);

    /// <summary>Inverse of <see cref="GainScaleQ16"/>.</summary>
    internal const int GainInvScaleQ16 = (65536 * (((88 - 2) * 128) / 6)) / 63;

    /// <summary>Maximum gain index increase per frame.</summary>
    internal const int MaxDeltaGainQuant = 36;

    /// <summary>Minimum gain index change per frame.</summary>
    internal const int MinDeltaGainQuant = -4;

    // ---------------------------------------------------------------------------------------
    // Excitation rate estimates (tables_pulses_per_block.c).
    // ---------------------------------------------------------------------------------------

    /// <summary>Maximum number of pulses a shell block may hold before downscaling.</summary>
    internal static readonly int[] MaxPulsesTable = [8, 10, 12, 16];

    /// <summary>Bit estimates, Q5, for the pulse counts of each rate level.</summary>
    internal static readonly byte[][] PulsesPerBlockBitsQ5 =
    [
        [31, 57, 107, 160, 205, 205, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255],
        [69, 47, 67, 111, 166, 205, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255],
        [82, 74, 79, 95, 109, 128, 145, 160, 173, 205, 205, 205, 224, 255, 255, 224, 255, 224],
        [125, 74, 59, 69, 97, 141, 182, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255],
        [173, 115, 85, 73, 76, 92, 115, 145, 173, 205, 224, 224, 255, 255, 255, 255, 255, 255],
        [166, 134, 113, 102, 101, 102, 107, 118, 125, 138, 145, 155, 166, 182, 192, 192, 205, 150],
        [224, 182, 134, 101, 83, 79, 85, 97, 120, 145, 173, 205, 224, 255, 255, 255, 255, 255],
        [255, 224, 192, 150, 120, 101, 92, 89, 93, 102, 118, 134, 160, 182, 192, 224, 224, 224],
        [255, 224, 224, 182, 155, 134, 118, 109, 104, 102, 106, 111, 118, 131, 145, 160, 173, 131],
    ];

    /// <summary>Bit estimates, Q5, for each rate level, indexed by active and voiced.</summary>
    internal static readonly byte[][] RateLevelsBitsQ5 =
    [
        [131, 74, 141, 79, 80, 138, 95, 104, 134],
        [95, 99, 91, 125, 93, 76, 123, 115, 123],
    ];

    /// <summary>Number of rate levels tested by the bitrate estimate.</summary>
    internal const int RateLevelCount = 9;
}
