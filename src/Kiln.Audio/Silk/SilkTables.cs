using System;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Silk;

/// <summary>
/// The probability tables and small codebooks the SILK bitstream decoder reads
/// (RFC 6716 §4.2.7), transcribed from the RFC's tables.
/// </summary>
/// <remarks>
/// <para>
/// The RFC publishes forward probability masses out of 256 (written <c>{...}/256</c>). The
/// decoder converts a table to cumulative frequencies on the fly in <see cref="DecodePdf"/>,
/// which is equivalent to the inverse-CDF form of RFC 6716 §4.1.3.3. Keeping the tables in the
/// RFC's own presentation makes each one directly checkable against its RFC table.
/// </para>
/// <para>
/// RFC 8251 changes no SILK table ("Only the C implementation is affected, not the English text
/// of the specification"). Its SILK errata are the stereo state reset (§3), the inverse-gain
/// wrap-around (§6) and the LSF stabilization wrap-around (§7); the last two live in synthesis
/// and stabilization, which this symbol decoder does not perform. The stereo reset is covered
/// by <see cref="SilkDecoder.Reset"/>.
/// </para>
/// </remarks>
internal static class SilkTables
{
    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.4, Table 4: per-frame LBRR flags.
    // ---------------------------------------------------------------------------------------

    /// <summary>Per-frame LBRR flag PDF for 40 ms Opus frames (RFC 6716 §4.2.4, Table 4).</summary>
    internal static readonly byte[] LbrrFlags40Pdf = [0, 53, 53, 150];

    /// <summary>Per-frame LBRR flag PDF for 60 ms Opus frames (RFC 6716 §4.2.4, Table 4).</summary>
    internal static readonly byte[] LbrrFlags60Pdf = [0, 41, 20, 29, 41, 15, 28, 82];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.1, Tables 6 and 7, and §4.2.7.2, Table 8: stereo.
    // ---------------------------------------------------------------------------------------

    /// <summary>Stage-1 stereo prediction weight PDF (RFC 6716 §4.2.7.1, Table 6).</summary>
    internal static readonly byte[] StereoPredJointPdf =
        [7, 2, 1, 1, 1, 10, 24, 8, 1, 1, 3, 23, 92, 23, 3, 1, 1, 8, 24, 10, 1, 1, 1, 2, 7];

    /// <summary>Stage-2 stereo prediction weight PDF (RFC 6716 §4.2.7.1, Table 6).</summary>
    internal static readonly byte[] StereoPredStage2Pdf = [85, 86, 85];

    /// <summary>Stage-3 stereo prediction weight PDF (RFC 6716 §4.2.7.1, Table 6).</summary>
    internal static readonly byte[] StereoPredStage3Pdf = [51, 51, 52, 51, 51];

    /// <summary>Stereo prediction weight table, Q13 (RFC 6716 §4.2.7.1, Table 7).</summary>
    internal static readonly short[] StereoWeightQ13 =
    [
        -13732, -10050, -8266, -7526, -6500, -5000, -2950, -820,
        820, 2950, 5000, 6500, 7526, 8266, 10050, 13732,
    ];

    /// <summary>Mid-only flag PDF (RFC 6716 §4.2.7.2, Table 8).</summary>
    internal static readonly byte[] MidOnlyPdf = [192, 64];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.3, Table 9: frame type.
    // ---------------------------------------------------------------------------------------

    /// <summary>Frame type PDF for an inactive regular SILK frame (RFC 6716 §4.2.7.3, Table 9).</summary>
    internal static readonly byte[] FrameTypeInactivePdf = [26, 230, 0, 0, 0, 0];

    /// <summary>Frame type PDF for an active or LBRR SILK frame (RFC 6716 §4.2.7.3, Table 9).</summary>
    internal static readonly byte[] FrameTypeActivePdf = [0, 0, 24, 74, 148, 10];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.4, Tables 11, 12 and 13: subframe gains.
    // ---------------------------------------------------------------------------------------

    /// <summary>Independent gain MSB PDFs, indexed by signal type (RFC 6716 §4.2.7.4, Table 11).</summary>
    internal static readonly byte[][] GainMsbPdf =
    [
        [32, 112, 68, 29, 12, 1, 1, 1],   // Inactive
        [2, 17, 45, 60, 62, 47, 19, 4],   // Unvoiced
        [1, 3, 26, 71, 94, 50, 9, 2],     // Voiced
    ];

    /// <summary>Independent gain LSB PDF, uniform over 8 values (RFC 6716 §4.2.7.4, Table 12).</summary>
    internal static readonly byte[] GainLsbPdf = [32, 32, 32, 32, 32, 32, 32, 32];

    /// <summary>Delta gain PDF, 41 values (RFC 6716 §4.2.7.4, Table 13).</summary>
    internal static readonly byte[] GainDeltaPdf =
    [
        6, 5, 11, 31, 132, 21, 8, 4, 3, 2, 2, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.5.1 and §4.2.7.5.2, Tables 14 through 19: normalized LSFs.
    // ---------------------------------------------------------------------------------------

    /// <summary>Stage-1 NLSF PDF, NB/MB, inactive or unvoiced (RFC 6716 §4.2.7.5.1, Table 14).</summary>
    internal static readonly byte[] NlsfStage1NbMbInactivePdf =
    [
        44, 34, 30, 19, 21, 12, 11, 3, 3, 2, 16, 2, 2, 1, 5, 2,
        1, 3, 3, 1, 1, 2, 2, 2, 3, 1, 9, 9, 2, 7, 2, 1,
    ];

    /// <summary>Stage-1 NLSF PDF, NB/MB, voiced (RFC 6716 §4.2.7.5.1, Table 14).</summary>
    internal static readonly byte[] NlsfStage1NbMbVoicedPdf =
    [
        1, 10, 1, 8, 3, 8, 8, 14, 13, 14, 1, 14, 12, 13, 11, 11,
        12, 11, 10, 10, 11, 8, 9, 8, 7, 8, 1, 1, 6, 1, 6, 5,
    ];

    /// <summary>Stage-1 NLSF PDF, WB, inactive or unvoiced (RFC 6716 §4.2.7.5.1, Table 14).</summary>
    internal static readonly byte[] NlsfStage1WbInactivePdf =
    [
        31, 21, 3, 17, 1, 8, 17, 4, 1, 18, 16, 4, 2, 3, 1, 10,
        1, 3, 16, 11, 16, 2, 2, 3, 2, 11, 1, 4, 9, 8, 7, 3,
    ];

    /// <summary>Stage-1 NLSF PDF, WB, voiced (RFC 6716 §4.2.7.5.1, Table 14).</summary>
    internal static readonly byte[] NlsfStage1WbVoicedPdf =
    [
        1, 4, 16, 5, 18, 11, 5, 14, 15, 1, 3, 12, 13, 14, 14, 6,
        14, 12, 2, 6, 1, 12, 12, 11, 10, 3, 10, 5, 1, 1, 1, 3,
    ];

    /// <summary>Stage-2 NLSF residual PDFs a..h for NB/MB (RFC 6716 §4.2.7.5.2, Table 15).</summary>
    internal static readonly byte[][] NlsfStage2NbMbPdf =
    [
        [1, 1, 1, 15, 224, 11, 1, 1, 1],   // a
        [1, 1, 2, 34, 183, 32, 1, 1, 1],   // b
        [1, 1, 4, 42, 149, 55, 2, 1, 1],   // c
        [1, 1, 8, 52, 123, 61, 8, 1, 1],   // d
        [1, 3, 16, 53, 101, 74, 6, 1, 1],  // e
        [1, 3, 17, 55, 90, 73, 15, 1, 1],  // f
        [1, 7, 24, 53, 74, 67, 26, 3, 1],  // g
        [1, 1, 18, 63, 78, 58, 30, 6, 1],  // h
    ];

    /// <summary>Stage-2 NLSF residual PDFs i..p for WB (RFC 6716 §4.2.7.5.2, Table 16).</summary>
    internal static readonly byte[][] NlsfStage2WbPdf =
    [
        [1, 1, 1, 9, 232, 9, 1, 1, 1],     // i
        [1, 1, 2, 28, 186, 35, 1, 1, 1],   // j
        [1, 1, 3, 42, 152, 53, 2, 1, 1],   // k
        [1, 1, 10, 49, 126, 65, 2, 1, 1],  // l
        [1, 4, 19, 48, 100, 77, 5, 1, 1],  // m
        [1, 1, 14, 54, 100, 72, 12, 1, 1], // n
        [1, 1, 15, 61, 87, 61, 25, 4, 1],  // o
        [1, 7, 21, 50, 77, 81, 17, 1, 1],  // p
    ];

    /// <summary>
    /// Stage-2 PDF selection for NB/MB, row I1 and coefficient k, as a letter a..h
    /// (RFC 6716 §4.2.7.5.2, Table 17).
    /// </summary>
    /// <remarks>The RFC table labels row 6 with "g" instead of "6"; it is index 6.</remarks>
    internal static readonly string[] NlsfStage2SelectNbMb =
    [
        "aaaaaaaaaa",
        "bdbccbcbbb",
        "cbbbbbbbbb",
        "bccccbcbbb",
        "cddddccccc",
        "afddccccbb",
        "accccccccb",
        "cdgeeefeff",
        "ceffefegee",
        "ceehefeffe",
        "edddcdcccc",
        "bffgefefff",
        "chegffffff",
        "chfffffgfe",
        "ddfeefefee",
        "cddffeeeee",
        "ceegefefff",
        "cfegfffefe",
        "chefefefff",
        "cfeghgfgfe",
        "dghegffgef",
        "chgeeefeff",
        "effeggfgfe",
        "cffgfgegee",
        "efffdheffe",
        "cdeffgeffe",
        "cdcddecddd",
        "bbcccccdcc",
        "effgggfgef",
        "dffeeeeddc",
        "cfdhffeefe",
        "eefefgfgfe",
    ];

    /// <summary>
    /// Stage-2 PDF selection for WB, row I1 and coefficient k, as a letter i..p
    /// (RFC 6716 §4.2.7.5.2, Table 18).
    /// </summary>
    internal static readonly string[] NlsfStage2SelectWb =
    [
        "iiiiiiiiiiiiiiii",
        "klllllkkkkkjjjil",
        "knnlpmmnknmnnmll",
        "ikjkkjjjjjiiiiij",
        "ionmompnmmmnnmml",
        "ilnnmllnllllllkm",
        "iiiiiiiiiiiiiiii",
        "ikolpknlmnnmllkl",
        "iokoomnmonmmnlll",
        "kjiiiiiiiiiiiiii",
        "ijiiiiiiiiiiiiij",
        "kklmnlllllllkkjl",
        "kkllmllllllllkjl",
        "lmmmommnlnmmnmlm",
        "iomnmpnkonpmmlnl",
        "ijijjjjjjjiiiiji",
        "jonpnmnlmnmmmllm",
        "jllmmllnkllnnnlm",
        "kllkkklkjkjkjjjm",
        "iklnllkkkjjiiiii",
        "lmlnllkkjjjjjkkm",
        "kolppmnmnlnllkll",
        "klnoolnlmmllllkm",
        "jllmmmmlnnnljjjj",
        "knloompmmnlmmlll",
        "iojjiiiiiiiiiiii",
        "ioolnknnlmmppmmm",
        "llplnmlllkklllkl",
        "iijiiikjkjjkkkjj",
        "ilknllklkjiijiij",
        "lnnmpnllklkkjiji",
        "klnlmlllkjkomiii",
    ];

    /// <summary>NLSF stage-2 index extension PDF (RFC 6716 §4.2.7.5.2, Table 19).</summary>
    internal static readonly byte[] NlsfExtensionPdf = [156, 60, 24, 9, 4, 2, 1];

    /// <summary>NLSF interpolation factor PDF, Q2 (RFC 6716 §4.2.7.5.5, Table 26).</summary>
    internal static readonly byte[] NlsfInterpolationPdf = [13, 22, 29, 11, 181];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.6.1, Tables 29 through 36: pitch lags.
    // ---------------------------------------------------------------------------------------

    /// <summary>High part of the absolute primary pitch lag, 32 entries (RFC 6716 §4.2.7.6.1, Table 29).</summary>
    internal static readonly byte[] PitchLagHighPdf =
    [
        3, 3, 6, 11, 21, 30, 32, 19, 11, 10, 12, 13, 13, 12, 11, 9,
        8, 7, 6, 4, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>Low part of the absolute primary pitch lag, NB: 4 entries (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly byte[] PitchLagLowPdfNb = [64, 64, 64, 64];

    /// <summary>Low part of the absolute primary pitch lag, MB: 6 entries (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly byte[] PitchLagLowPdfMb = [43, 42, 43, 43, 42, 43];

    /// <summary>Low part of the absolute primary pitch lag, WB: 8 entries (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly byte[] PitchLagLowPdfWb = [32, 32, 32, 32, 32, 32, 32, 32];

    /// <summary>Low part of the primary lag PDF by bandwidth, indexed NB, MB, WB (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly byte[][] PitchLagLowPdf = [PitchLagLowPdfNb, PitchLagLowPdfMb, PitchLagLowPdfWb];

    /// <summary>Primary lag scale factor by bandwidth, indexed NB, MB, WB (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly int[] PitchLagScale = [4, 6, 8];

    /// <summary>Minimum primary lag by bandwidth, indexed NB, MB, WB (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly int[] PitchLagMin = [16, 24, 32];

    /// <summary>Maximum primary lag by bandwidth, indexed NB, MB, WB (RFC 6716 §4.2.7.6.1, Table 30).</summary>
    internal static readonly int[] PitchLagMax = [144, 216, 288];

    /// <summary>Primary pitch lag change PDF, 21 entries (RFC 6716 §4.2.7.6.1, Table 31).</summary>
    internal static readonly byte[] PitchLagDeltaPdf =
        [46, 2, 2, 3, 4, 6, 10, 15, 26, 38, 30, 22, 15, 10, 7, 6, 4, 4, 2, 2, 2];

    /// <summary>Subframe pitch contour PDF, NB 10 ms (RFC 6716 §4.2.7.6.1, Table 32).</summary>
    internal static readonly byte[] PitchContourPdfNb10 = [143, 50, 63];

    /// <summary>Subframe pitch contour PDF, NB 20 ms (RFC 6716 §4.2.7.6.1, Table 32).</summary>
    internal static readonly byte[] PitchContourPdfNb20 = [68, 12, 21, 17, 19, 22, 30, 24, 17, 16, 10];

    /// <summary>Subframe pitch contour PDF, MB or WB 10 ms (RFC 6716 §4.2.7.6.1, Table 32).</summary>
    internal static readonly byte[] PitchContourPdfMbWb10 = [91, 46, 39, 19, 14, 12, 8, 7, 6, 5, 5, 4];

    /// <summary>Subframe pitch contour PDF, MB or WB 20 ms (RFC 6716 §4.2.7.6.1, Table 32).</summary>
    internal static readonly byte[] PitchContourPdfMbWb20 =
    [
        33, 22, 18, 16, 15, 14, 14, 13, 13, 10, 9, 9, 8, 6, 6, 6, 5,
        4, 4, 4, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1,
    ];

    /// <summary>Contour offsets, NB 10 ms, 3 vectors of 2 (RFC 6716 §4.2.7.6.1, Table 33).</summary>
    internal static readonly sbyte[] PitchContourNb10 = [0, 0, 1, 0, 0, 1];

    /// <summary>Contour offsets, NB 20 ms, 11 vectors of 4 (RFC 6716 §4.2.7.6.1, Table 34).</summary>
    internal static readonly sbyte[] PitchContourNb20 =
    [
        0, 0, 0, 0,
        2, 1, 0, -1,
        -1, 0, 1, 2,
        -1, 0, 0, 1,
        -1, 0, 0, 0,
        0, 0, 0, 1,
        0, 0, 1, 1,
        1, 1, 0, 0,
        1, 0, 0, 0,
        0, 0, 0, -1,
        1, 0, 0, -1,
    ];

    /// <summary>Contour offsets, MB or WB 10 ms, 12 vectors of 2 (RFC 6716 §4.2.7.6.1, Table 35).</summary>
    internal static readonly sbyte[] PitchContourMbWb10 =
    [
        0, 0,
        0, 1,
        1, 0,
        -1, 1,
        1, -1,
        -1, 2,
        2, -1,
        -2, 2,
        2, -2,
        -2, 3,
        3, -2,
        -3, 3,
    ];

    /// <summary>Contour offsets, MB or WB 20 ms, 34 vectors of 4 (RFC 6716 §4.2.7.6.1, Table 36).</summary>
    internal static readonly sbyte[] PitchContourMbWb20 =
    [
        0, 0, 0, 0,
        0, 0, 1, 1,
        1, 1, 0, 0,
        -1, 0, 0, 0,
        0, 0, 0, 1,
        1, 0, 0, 0,
        -1, 0, 0, 1,
        0, 0, 0, -1,
        -1, 0, 1, 2,
        1, 0, 0, -1,
        -2, -1, 1, 2,
        2, 1, 0, -1,
        -2, 0, 0, 2,
        -2, 0, 1, 3,
        2, 1, -1, -2,
        -3, -1, 1, 3,
        2, 0, 0, -2,
        3, 1, 0, -2,
        -3, -1, 2, 4,
        -4, -1, 1, 4,
        3, 1, -1, -3,
        -4, -1, 2, 5,
        4, 2, -1, -3,
        4, 1, -1, -4,
        -5, -1, 2, 6,
        5, 2, -1, -4,
        -6, -2, 2, 6,
        -5, -2, 2, 5,
        6, 2, -1, -5,
        -7, -2, 3, 8,
        6, 2, -2, -6,
        5, 2, -2, -5,
        8, 3, -2, -7,
        -9, -3, 3, 9,
    ];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.6.2 and §4.2.7.6.3, Tables 37, 38 and 42: LTP.
    // ---------------------------------------------------------------------------------------

    /// <summary>Periodicity index PDF (RFC 6716 §4.2.7.6.2, Table 37).</summary>
    internal static readonly byte[] LtpPeriodicityPdf = [77, 80, 99];

    /// <summary>LTP filter index PDFs, indexed by periodicity (RFC 6716 §4.2.7.6.2, Table 38).</summary>
    internal static readonly byte[][] LtpFilterPdf =
    [
        [185, 15, 13, 13, 9, 9, 6, 6],
        [57, 34, 21, 20, 15, 13, 12, 13, 10, 10, 9, 10, 9, 8, 7, 8],
        [
            15, 16, 14, 12, 12, 12, 11, 11, 11, 10, 9, 9, 9, 9, 8, 8,
            8, 8, 7, 7, 6, 6, 5, 4, 5, 4, 4, 4, 3, 4, 3, 2,
        ],
    ];

    /// <summary>LTP scaling parameter PDF (RFC 6716 §4.2.7.6.3, Table 42).</summary>
    internal static readonly byte[] LtpScalingPdf = [128, 64, 64];

    /// <summary>LTP scaling factors, Q14, indexed as the Table 42 symbol (RFC 6716 §4.2.7.6.3).</summary>
    internal static readonly int[] LtpScalingQ14 = [15565, 12288, 8192];

    /// <summary>Default LTP scaling factor when the frame does not code one (RFC 6716 §4.2.7.6.3).</summary>
    internal const int LtpScalingDefaultQ14 = 15565;

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.7, Table 43: LCG seed.
    // ---------------------------------------------------------------------------------------

    /// <summary>LCG seed PDF, uniform over 4 values (RFC 6716 §4.2.7.7, Table 43).</summary>
    internal static readonly byte[] SeedPdf = [64, 64, 64, 64];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.8, Tables 44 through 52: excitation.
    // ---------------------------------------------------------------------------------------

    /// <summary>Rate level PDF for inactive and unvoiced frames (RFC 6716 §4.2.7.8.1, Table 45).</summary>
    internal static readonly byte[] RateLevelInactivePdf = [15, 51, 12, 46, 45, 13, 33, 27, 14];

    /// <summary>Rate level PDF for voiced frames (RFC 6716 §4.2.7.8.1, Table 45).</summary>
    internal static readonly byte[] RateLevelVoicedPdf = [33, 30, 36, 17, 34, 49, 18, 21, 18];

    /// <summary>
    /// Pulse count PDFs for rate levels 0 through 10; each row has 18 entries, the last being
    /// the escape value 17 (RFC 6716 §4.2.7.8.2, Table 46).
    /// </summary>
    internal static readonly byte[][] PulseCountPdf =
    [
        [131, 74, 25, 8, 3, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [58, 93, 60, 23, 7, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [43, 51, 46, 33, 24, 16, 11, 8, 6, 3, 3, 3, 2, 1, 1, 2, 1, 2],
        [17, 52, 71, 57, 31, 12, 5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        [6, 21, 41, 53, 49, 35, 21, 11, 6, 3, 2, 2, 1, 1, 1, 1, 1, 1],
        [7, 14, 22, 28, 29, 28, 25, 20, 17, 13, 11, 9, 7, 5, 4, 4, 3, 10],
        [2, 5, 14, 29, 42, 46, 41, 31, 19, 11, 6, 3, 2, 1, 1, 1, 1, 1],
        [1, 2, 4, 10, 19, 29, 35, 37, 34, 28, 20, 14, 8, 5, 4, 2, 2, 2],
        [1, 2, 2, 5, 9, 14, 20, 24, 27, 28, 26, 23, 20, 15, 11, 8, 6, 15],
        [1, 1, 1, 6, 27, 58, 56, 39, 25, 14, 10, 6, 3, 3, 2, 1, 1, 2],
        [2, 1, 6, 27, 58, 56, 39, 25, 14, 10, 6, 3, 3, 2, 1, 1, 2, 0],
    ];

    /// <summary>Pulse count split PDFs, 16-sample partitions, indexed by pulse count (RFC 6716 §4.2.7.8.3, Table 47).</summary>
    internal static readonly byte[][] PulseSplit16Pdf =
    [
        [126, 130],
        [56, 142, 58],
        [25, 101, 104, 26],
        [12, 60, 108, 64, 12],
        [7, 35, 84, 87, 37, 6],
        [4, 20, 59, 86, 63, 21, 3],
        [3, 12, 38, 72, 75, 42, 12, 2],
        [2, 8, 25, 54, 73, 59, 27, 7, 1],
        [2, 5, 17, 39, 63, 65, 42, 18, 4, 1],
        [1, 4, 12, 28, 49, 63, 54, 30, 11, 3, 1],
        [1, 4, 8, 20, 37, 55, 57, 41, 22, 8, 2, 1],
        [1, 3, 7, 15, 28, 44, 53, 48, 33, 16, 6, 1, 1],
        [1, 2, 6, 12, 21, 35, 47, 48, 40, 25, 12, 5, 1, 1],
        [1, 1, 4, 10, 17, 27, 37, 47, 43, 33, 21, 9, 4, 1, 1],
        [1, 1, 1, 8, 14, 22, 33, 40, 43, 38, 28, 16, 8, 1, 1, 1],
        [1, 1, 1, 1, 13, 18, 27, 36, 41, 41, 34, 24, 14, 1, 1, 1, 1],
    ];

    /// <summary>Pulse count split PDFs, 8-sample partitions, indexed by pulse count (RFC 6716 §4.2.7.8.3, Table 48).</summary>
    internal static readonly byte[][] PulseSplit8Pdf =
    [
        [127, 129],
        [53, 149, 54],
        [22, 105, 106, 23],
        [11, 61, 111, 63, 10],
        [6, 35, 86, 88, 36, 5],
        [4, 20, 59, 87, 62, 21, 3],
        [3, 13, 40, 71, 73, 41, 13, 2],
        [3, 9, 27, 53, 70, 56, 28, 9, 1],
        [3, 8, 19, 37, 57, 61, 44, 20, 6, 1],
        [3, 7, 15, 28, 44, 54, 49, 33, 17, 5, 1],
        [1, 7, 13, 22, 34, 46, 48, 38, 28, 14, 4, 1],
        [1, 1, 11, 22, 27, 35, 42, 47, 33, 25, 10, 1, 1],
        [1, 1, 6, 14, 26, 37, 43, 43, 37, 26, 14, 6, 1, 1],
        [1, 1, 4, 10, 20, 31, 40, 42, 40, 31, 20, 10, 4, 1, 1],
        [1, 1, 3, 8, 16, 26, 35, 38, 38, 35, 26, 16, 8, 3, 1, 1],
        [1, 1, 2, 6, 12, 21, 30, 36, 38, 36, 30, 21, 12, 6, 2, 1, 1],
    ];

    /// <summary>Pulse count split PDFs, 4-sample partitions, indexed by pulse count (RFC 6716 §4.2.7.8.3, Table 49).</summary>
    internal static readonly byte[][] PulseSplit4Pdf =
    [
        [127, 129],
        [49, 157, 50],
        [20, 107, 109, 20],
        [11, 60, 113, 62, 10],
        [7, 36, 84, 87, 36, 6],
        [6, 24, 57, 82, 60, 23, 4],
        [5, 18, 39, 64, 68, 42, 16, 4],
        [6, 14, 29, 47, 61, 52, 30, 14, 3],
        [1, 15, 23, 35, 51, 50, 40, 30, 10, 1],
        [1, 1, 21, 32, 42, 52, 46, 41, 18, 1, 1],
        [1, 6, 16, 27, 36, 42, 42, 36, 27, 16, 6, 1],
        [1, 5, 12, 21, 31, 38, 40, 38, 31, 21, 12, 5, 1],
        [1, 3, 9, 17, 26, 34, 38, 38, 34, 26, 17, 9, 3, 1],
        [1, 3, 7, 14, 22, 29, 34, 36, 34, 29, 22, 14, 7, 3, 1],
        [1, 2, 5, 11, 18, 25, 31, 35, 35, 31, 25, 18, 11, 5, 2, 1],
        [1, 1, 4, 9, 15, 21, 28, 32, 34, 32, 28, 21, 15, 9, 4, 1, 1],
    ];

    /// <summary>Pulse count split PDFs, 2-sample partitions, indexed by pulse count (RFC 6716 §4.2.7.8.3, Table 50).</summary>
    internal static readonly byte[][] PulseSplit2Pdf =
    [
        [128, 128],
        [42, 172, 42],
        [21, 107, 107, 21],
        [12, 60, 112, 61, 11],
        [8, 34, 86, 86, 35, 7],
        [8, 23, 55, 90, 55, 20, 5],
        [5, 15, 38, 72, 72, 36, 15, 3],
        [6, 12, 27, 52, 77, 47, 20, 10, 5],
        [6, 19, 28, 35, 40, 40, 35, 28, 19, 6],
        [4, 14, 22, 31, 37, 40, 37, 31, 22, 14, 4],
        [3, 10, 18, 26, 33, 38, 38, 33, 26, 18, 10, 3],
        [2, 8, 13, 21, 29, 36, 38, 36, 29, 21, 13, 8, 2],
        [1, 5, 10, 17, 25, 32, 38, 38, 32, 25, 17, 10, 5, 1],
        [1, 4, 7, 13, 21, 29, 35, 36, 35, 29, 21, 13, 7, 4, 1],
        [1, 2, 5, 10, 17, 25, 32, 36, 36, 32, 25, 17, 10, 5, 2, 1],
        [1, 2, 4, 7, 13, 21, 28, 34, 36, 34, 28, 21, 13, 7, 4, 2, 1],
    ];

    /// <summary>Excitation LSB PDF (RFC 6716 §4.2.7.8.4, Table 51).</summary>
    internal static readonly byte[] LsbPdf = [136, 120];

    /// <summary>
    /// Excitation sign PDFs indexed by signal type, quantization offset type, and
    /// min(pulse count, 6) (RFC 6716 §4.2.7.8.5, Table 52).
    /// </summary>
    internal static readonly byte[][][][] SignPdf =
    [
        // Inactive: low and high quantization offset.
        new byte[][][]
        {
            new byte[][] { [2, 254], [207, 49], [189, 67], [179, 77], [174, 82], [163, 93], [157, 99] },
            new byte[][] { [58, 198], [245, 11], [238, 18], [232, 24], [225, 31], [220, 36], [211, 45] },
        },
        // Unvoiced: low and high quantization offset.
        new byte[][][]
        {
            new byte[][] { [1, 255], [210, 46], [190, 66], [178, 78], [169, 87], [162, 94], [152, 104] },
            new byte[][] { [48, 208], [242, 14], [235, 21], [224, 32], [214, 42], [205, 51], [190, 66] },
        },
        // Voiced: low and high quantization offset.
        new byte[][][]
        {
            new byte[][] { [1, 255], [162, 94], [152, 104], [147, 109], [144, 112], [141, 115], [138, 118] },
            new byte[][] { [8, 248], [203, 53], [187, 69], [176, 80], [168, 88], [161, 95], [154, 102] },
        },
    ];

    /// <summary>Excitation quantization offset, Q23, indexed by signal and offset type (RFC 6716 §4.2.7.8.6, Table 53).</summary>
    internal static readonly int[][] QuantizationOffsetQ23 =
    [
        [25, 60], // Inactive: low, high
        [25, 60], // Unvoiced: low, high
        [8, 25],  // Voiced: low, high
    ];

    /// <summary>
    /// Number of 16-sample shell blocks in one SILK frame, indexed by bandwidth (NB, MB, WB)
    /// and SILK frame size in milliseconds (10 or 20) (RFC 6716 §4.2.7.8, Table 44).
    /// </summary>
    internal static readonly int[][] ShellBlockCount =
    [
        [5, 10],  // NB: 10 ms, 20 ms
        [8, 15],  // MB: 10 ms, 20 ms
        [10, 20], // WB: 10 ms, 20 ms
    ];

    static SilkTables()
    {
        // These selection tables are long and dense, so validate their shape once instead of
        // risking an index error deep inside the decoder.
        ValidateSelection(NlsfStage2SelectNbMb, rowLength: 10, symbolCount: NlsfStage2NbMbPdf.Length, firstLetter: 'a');
        ValidateSelection(NlsfStage2SelectWb, rowLength: 16, symbolCount: NlsfStage2WbPdf.Length, firstLetter: 'i');
    }

    /// <summary>
    /// Decodes one symbol from a forward probability table out of 256 (RFC 6716 §4.1.3.3),
    /// advancing the range decoder.
    /// </summary>
    /// <param name="dec">The range decoder.</param>
    /// <param name="pdf">Forward probability masses summing to 256.</param>
    /// <returns>The decoded symbol index, 0 to <c>pdf.Length - 1</c>.</returns>
    internal static int DecodePdf(ref RangeDecoder dec, ReadOnlySpan<byte> pdf)
    {
        // RFC 6716 §4.1.3.3: the symbol is the k with fh[k-1] <= fs < fh[k]. Doing the search
        // here over the forward table avoids storing a second, derived inverse table.
        const uint ft = 256;
        uint fs = dec.Decode(ft);
        uint fl = 0;
        for (int k = 0; k < pdf.Length; k++)
        {
            uint fh = fl + pdf[k];
            if (fs < fh)
            {
                dec.Update(fl, fh, ft);
                return k;
            }

            fl = fh;
        }

        // The table sums to less than 256: fall back to the last symbol rather than stall.
        dec.Update(fl, ft, ft);
        return pdf.Length - 1;
    }

    /// <summary>Returns the pulse-split PDF for a partition size and pulse count (Tables 47 to 50).</summary>
    /// <param name="partitionSize">Partition size: 16, 8, 4 or 2.</param>
    /// <param name="pulseCount">Pulse count in the partition, 1 to 16.</param>
    internal static ReadOnlySpan<byte> PulseSplitPdf(int partitionSize, int pulseCount) => partitionSize switch
    {
        16 => PulseSplit16Pdf[pulseCount - 1],
        8 => PulseSplit8Pdf[pulseCount - 1],
        4 => PulseSplit4Pdf[pulseCount - 1],
        2 => PulseSplit2Pdf[pulseCount - 1],
        _ => throw new ArgumentOutOfRangeException(nameof(partitionSize), partitionSize, "Unknown partition size."),
    };

    /// <summary>
    /// Approximates <c>2**(inLogQ7/128.0)</c> in the fixed-point described in RFC 6716 §4.2.7.4.
    /// </summary>
    /// <param name="inLogQ7">A Q7 logarithm.</param>
    /// <returns>The linear value.</returns>
    internal static int Log2Lin(int inLogQ7)
    {
        int i = inLogQ7 >> 7;
        int f = inLogQ7 & 127;
        return (1 << i) + ((((-174 * f * (128 - f)) >> 16) + f) * ((1 << i) >> 7));
    }

    private static void ValidateSelection(string[] rows, int rowLength, int symbolCount, int firstLetter)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i];
            if (row.Length != rowLength)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"NLSF stage-2 selection row {i} has length {row.Length}, not {rowLength}."));
            }

            foreach (char letter in row)
            {
                if (letter < firstLetter || letter >= firstLetter + symbolCount)
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant($"NLSF stage-2 selection row {i} names an unknown PDF."));
                }
            }
        }
    }
}
