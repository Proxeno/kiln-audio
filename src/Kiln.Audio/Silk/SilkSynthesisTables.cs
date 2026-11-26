// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Silk;

/// <summary>
/// The codebooks and constant tables the SILK reconstruction stages read
/// (RFC 6716 §4.2.7.5 through §4.2.7.9), transcribed from the RFC's tables.
/// </summary>
/// <remarks>
/// The symbol decoder (<see cref="SilkTables"/>) already owns the entropy tables. This class holds
/// only what synthesis needs: the stage-1 NLSF codebooks (Tables 23 and 24), the LSF prediction
/// weights (Tables 20 through 22), the minimum LSF spacings (Table 25), the Q12 cosine table
/// (Table 28), the LTP filter codebooks (Tables 39 through 41), the excitation quantization
/// offsets (Table 53), and the two NLSF quantization step sizes.
/// </remarks>
internal static class SilkSynthesisTables
{
    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.5.3, Tables 23 and 24: stage-1 NLSF codebooks, Q8.
    // ---------------------------------------------------------------------------------------

    /// <summary>NB/MB stage-1 NLSF codebook, 32 vectors of order 10 (RFC 6716 §4.2.7.5.3, Table 23).</summary>
    internal static readonly byte[] NlsfCb1NbMbQ8 =
    [
        12, 35, 60, 83, 108, 132, 157, 180, 206, 228,
        15, 32, 55, 77, 101, 125, 151, 175, 201, 225,
        19, 42, 66, 89, 114, 137, 162, 184, 209, 230,
        12, 25, 50, 72, 97, 120, 147, 172, 200, 223,
        26, 44, 69, 90, 114, 135, 159, 180, 205, 225,
        13, 22, 53, 80, 106, 130, 156, 180, 205, 228,
        15, 25, 44, 64, 90, 115, 142, 168, 196, 222,
        19, 24, 62, 82, 100, 120, 145, 168, 190, 214,
        22, 31, 50, 79, 103, 120, 151, 170, 203, 227,
        21, 29, 45, 65, 106, 124, 150, 171, 196, 224,
        30, 49, 75, 97, 121, 142, 165, 186, 209, 229,
        19, 25, 52, 70, 93, 116, 143, 166, 192, 219,
        26, 34, 62, 75, 97, 118, 145, 167, 194, 217,
        25, 33, 56, 70, 91, 113, 143, 165, 196, 223,
        21, 34, 51, 72, 97, 117, 145, 171, 196, 222,
        20, 29, 50, 67, 90, 117, 144, 168, 197, 221,
        22, 31, 48, 66, 95, 117, 146, 168, 196, 222,
        24, 33, 51, 77, 116, 134, 158, 180, 200, 224,
        21, 28, 70, 87, 106, 124, 149, 170, 194, 217,
        26, 33, 53, 64, 83, 117, 152, 173, 204, 225,
        27, 34, 65, 95, 108, 129, 155, 174, 210, 225,
        20, 26, 72, 99, 113, 131, 154, 176, 200, 219,
        34, 43, 61, 78, 93, 114, 155, 177, 205, 229,
        23, 29, 54, 97, 124, 138, 163, 179, 209, 229,
        30, 38, 56, 89, 118, 129, 158, 178, 200, 231,
        21, 29, 49, 63, 85, 111, 142, 163, 193, 222,
        27, 48, 77, 103, 133, 158, 179, 196, 215, 232,
        29, 47, 74, 99, 124, 151, 176, 198, 220, 237,
        33, 42, 61, 76, 93, 121, 155, 174, 207, 225,
        29, 53, 87, 112, 136, 154, 170, 188, 208, 227,
        24, 30, 52, 84, 131, 150, 166, 186, 203, 229,
        37, 48, 64, 84, 104, 118, 156, 177, 201, 230,
    ];

    /// <summary>WB stage-1 NLSF codebook, 32 vectors of order 16 (RFC 6716 §4.2.7.5.3, Table 24).</summary>
    internal static readonly byte[] NlsfCb1WbQ8 =
    [
        7, 23, 38, 54, 69, 85, 100, 116, 131, 147, 162, 178, 193, 208, 223, 239,
        13, 25, 41, 55, 69, 83, 98, 112, 127, 142, 157, 171, 187, 203, 220, 236,
        15, 21, 34, 51, 61, 78, 92, 106, 126, 136, 152, 167, 185, 205, 225, 240,
        10, 21, 36, 50, 63, 79, 95, 110, 126, 141, 157, 173, 189, 205, 221, 237,
        17, 20, 37, 51, 59, 78, 89, 107, 123, 134, 150, 164, 184, 205, 224, 240,
        10, 15, 32, 51, 67, 81, 96, 112, 129, 142, 158, 173, 189, 204, 220, 236,
        8, 21, 37, 51, 65, 79, 98, 113, 126, 138, 155, 168, 179, 192, 209, 218,
        12, 15, 34, 55, 63, 78, 87, 108, 118, 131, 148, 167, 185, 203, 219, 236,
        16, 19, 32, 36, 56, 79, 91, 108, 118, 136, 154, 171, 186, 204, 220, 237,
        11, 28, 43, 58, 74, 89, 105, 120, 135, 150, 165, 180, 196, 211, 226, 241,
        6, 16, 33, 46, 60, 75, 92, 107, 123, 137, 156, 169, 185, 199, 214, 225,
        11, 19, 30, 44, 57, 74, 89, 105, 121, 135, 152, 169, 186, 202, 218, 234,
        12, 19, 29, 46, 57, 71, 88, 100, 120, 132, 148, 165, 182, 199, 216, 233,
        17, 23, 35, 46, 56, 77, 92, 106, 123, 134, 152, 167, 185, 204, 222, 237,
        14, 17, 45, 53, 63, 75, 89, 107, 115, 132, 151, 171, 188, 206, 221, 240,
        9, 16, 29, 40, 56, 71, 88, 103, 119, 137, 154, 171, 189, 205, 222, 237,
        16, 19, 36, 48, 57, 76, 87, 105, 118, 132, 150, 167, 185, 202, 218, 236,
        12, 17, 29, 54, 71, 81, 94, 104, 126, 136, 149, 164, 182, 201, 221, 237,
        15, 28, 47, 62, 79, 97, 115, 129, 142, 155, 168, 180, 194, 208, 223, 238,
        8, 14, 30, 45, 62, 78, 94, 111, 127, 143, 159, 175, 192, 207, 223, 239,
        17, 30, 49, 62, 79, 92, 107, 119, 132, 145, 160, 174, 190, 204, 220, 235,
        14, 19, 36, 45, 61, 76, 91, 108, 121, 138, 154, 172, 189, 205, 222, 238,
        12, 18, 31, 45, 60, 76, 91, 107, 123, 138, 154, 171, 187, 204, 221, 236,
        13, 17, 31, 43, 53, 70, 83, 103, 114, 131, 149, 167, 185, 203, 220, 237,
        17, 22, 35, 42, 58, 78, 93, 110, 125, 139, 155, 170, 188, 206, 224, 240,
        8, 15, 34, 50, 67, 83, 99, 115, 131, 146, 162, 178, 193, 209, 224, 239,
        13, 16, 41, 66, 73, 86, 95, 111, 128, 137, 150, 163, 183, 206, 225, 241,
        17, 25, 37, 52, 63, 75, 92, 102, 119, 132, 144, 160, 175, 191, 212, 231,
        19, 31, 49, 65, 83, 100, 117, 133, 147, 161, 174, 187, 200, 213, 227, 242,
        18, 31, 52, 68, 88, 103, 117, 126, 138, 149, 163, 177, 192, 207, 223, 239,
        16, 29, 47, 61, 76, 90, 106, 119, 133, 147, 161, 176, 193, 209, 224, 240,
        15, 21, 35, 50, 61, 73, 86, 97, 110, 119, 129, 141, 175, 198, 218, 237,
    ];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.5.3, Table 20, and §4.2.7.5.2, Tables 21 and 22: prediction weights.
    // ---------------------------------------------------------------------------------------

    /// <summary>NB/MB prediction weight codebook, Q8, indexed by coefficient and A/B (RFC 6716 §4.2.7.5.3, Table 20).</summary>
    internal static readonly byte[] NlsfPredNbMbQ8 =
    [
        179, 138, 140, 148, 151, 149, 153, 151, 163,
        116, 67, 82, 59, 92, 72, 100, 89, 92,
    ];

    /// <summary>WB prediction weight codebook, Q8, indexed by coefficient and C/D (RFC 6716 §4.2.7.5.3, Table 20).</summary>
    internal static readonly byte[] NlsfPredWbQ8 =
    [
        175, 148, 160, 176, 178, 173, 174, 164, 177, 174, 196, 182, 198, 192, 182,
        68, 62, 66, 60, 72, 117, 85, 90, 118, 136, 151, 142, 160, 142, 155,
    ];

    /// <summary>
    /// Which prediction weight (A or B) each NB/MB coefficient uses for each stage-1 index
    /// (RFC 6716 §4.2.7.5.2, Table 21).
    /// </summary>
    internal static readonly string[] NlsfPredSelectNbMb =
    [
        "ABAAAAAAA", "BAAAAAAAA", "AAAAAAAAA", "BBBAAAABA", "ABAAAAAAA", "ABAAAAAAA",
        "BABBAAABA", "ABBAABBAA", "AABBABABB", "AABBAABBB", "AAAAAAAAA", "ABABBBBBA",
        "ABABBBBBA", "ABBBBBBBA", "BABBABBBB", "ABBBBBABA", "AABBABABA", "AABBBABBB",
        "ABBAABBBA", "AAABBBABA", "ABBAABABA", "ABBAAABBA", "AAAAABBBB", "AABBAAABB",
        "AAABABBBB", "ABBBBBBBA", "AAAAAAAAA", "AAAAAAAAA", "AABABBABA", "BAABAAAAA",
        "AAABBABAB", "BABBABBBB",
    ];

    /// <summary>
    /// Which prediction weight (C or D) each WB coefficient uses for each stage-1 index
    /// (RFC 6716 §4.2.7.5.2, Table 22).
    /// </summary>
    internal static readonly string[] NlsfPredSelectWb =
    [
        "CCCCCCCCCCCCCCD", "CCCCCCCCCCCCCCC", "CCDCCDDDCDDDDCC", "CCCCCCCCCCCCDCC",
        "CDDCDCDDCDDDDDC", "CCDCCCCCCCCCCCC", "DCCCCCCCCCCDCDC", "CDDCCCDCDDDCDCD",
        "CDCDDCDCDCDDDDD", "CCCCCCCCCCCCCCD", "CDCCCCCCCCCCCCC", "CCDCDDDDDDDCDCC",
        "CCDCCDCDCDCCDCC", "CCCCDDCDCDDDDCC", "CDCCCDDCDDDCDDD", "CCDDCCCCCCCCDDC",
        "CDDCDCDDDDDCDCC", "CCDCCCCDCCDDDCC", "CCCCCCCCCCCCCCD", "CCCCCCCCCCCCDCC",
        "CCCCCCCCCCCCCCC", "CDCDCDDCDCDCDDC", "CCDDDDCDDCCDDCC", "CDDCDCDCDCCCCDC",
        "CCCDDCDCDDDDDDD", "CCCCCCCCCCCCCCD", "CDDCCCDDCCDDDDD", "CCCCCDCDDDDCDDD",
        "CCCCCCCCCCCCCCD", "CCCCCCCCCCCCCCD", "DCCCCCCCCCCDCCC", "CCDCCDDDCCDCCDC",
    ];

    /// <summary>NB/MB minimum LSF spacings, Q15 (RFC 6716 §4.2.7.5.4, Table 25).</summary>
    internal static readonly short[] NlsfDeltaMinNbMbQ15 =
        [250, 3, 6, 3, 3, 3, 4, 3, 3, 3, 461];

    /// <summary>WB minimum LSF spacings, Q15 (RFC 6716 §4.2.7.5.4, Table 25).</summary>
    internal static readonly short[] NlsfDeltaMinWbQ15 =
        [100, 3, 40, 3, 3, 3, 5, 14, 14, 10, 11, 3, 8, 9, 7, 3, 347];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.5.6, Table 28: Q12 cosine table for LSF conversion.
    // ---------------------------------------------------------------------------------------

    /// <summary>Q12 cosine table, 129 entries (RFC 6716 §4.2.7.5.6, Table 28).</summary>
    internal static readonly short[] LsfCosQ12 =
    [
        8192, 8190, 8182, 8170, 8152, 8130, 8104, 8072, 8034, 7994, 7946, 7896, 7840, 7778,
        7714, 7644, 7568, 7490, 7406, 7318, 7226, 7128, 7026, 6922, 6812, 6698, 6580, 6458,
        6332, 6204, 6070, 5934, 5792, 5648, 5502, 5352, 5198, 5040, 4880, 4718, 4552, 4382,
        4212, 4038, 3862, 3684, 3502, 3320, 3136, 2948, 2760, 2570, 2378, 2186, 1990, 1794,
        1598, 1400, 1202, 1002, 802, 602, 402, 202, 0, -202, -402, -602, -802, -1002, -1202,
        -1400, -1598, -1794, -1990, -2186, -2378, -2570, -2760, -2948, -3136, -3320, -3502,
        -3684, -3862, -4038, -4212, -4382, -4552, -4718, -4880, -5040, -5198, -5352, -5502,
        -5648, -5792, -5934, -6070, -6204, -6332, -6458, -6580, -6698, -6812, -6922, -7026,
        -7128, -7226, -7318, -7406, -7490, -7568, -7644, -7714, -7778, -7840, -7896, -7946,
        -7994, -8034, -8072, -8104, -8130, -8152, -8170, -8182, -8190, -8192,
    ];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.6.2, Tables 39 through 41: LTP filter codebooks, Q7.
    // ---------------------------------------------------------------------------------------

    /// <summary>LTP filter codebooks, Q7, flattened as index * 5 + tap (RFC 6716 §4.2.7.6.2, Tables 39-41).</summary>
    internal static readonly sbyte[][] LtpCodebookQ7 =
    [
        // Periodicity 0, 8 entries.
        [
            4, 6, 24, 7, 5,
            0, 0, 2, 0, 0,
            12, 28, 41, 13, -4,
            -9, 15, 42, 25, 14,
            1, -2, 62, 41, -9,
            -10, 37, 65, -4, 3,
            -6, 4, 66, 7, -8,
            16, 14, 38, -3, 33,
        ],
        // Periodicity 1, 16 entries.
        [
            13, 22, 39, 23, 12,
            -1, 36, 64, 27, -6,
            -7, 10, 55, 43, 17,
            1, 1, 8, 1, 1,
            6, -11, 74, 53, -9,
            -12, 55, 76, -12, 8,
            -3, 3, 93, 27, -4,
            26, 39, 59, 3, -8,
            2, 0, 77, 11, 9,
            -8, 22, 44, -6, 7,
            40, 9, 26, 3, 9,
            -7, 20, 101, -7, 4,
            3, -8, 42, 26, 0,
            -15, 33, 68, 2, 23,
            -2, 55, 46, -2, 15,
            3, -1, 21, 16, 41,
        ],
        // Periodicity 2, 32 entries.
        [
            -6, 27, 61, 39, 5,
            -11, 42, 88, 4, 1,
            -2, 60, 65, 6, -4,
            -1, -5, 73, 56, 1,
            -9, 19, 94, 29, -9,
            0, 12, 99, 6, 4,
            8, -19, 102, 46, -13,
            3, 2, 13, 3, 2,
            9, -21, 84, 72, -18,
            -11, 46, 104, -22, 8,
            18, 38, 48, 23, 0,
            -16, 70, 83, -21, 11,
            5, -11, 117, 22, -8,
            -6, 23, 117, -12, 3,
            3, -8, 95, 28, 4,
            -10, 15, 77, 60, -15,
            -1, 4, 124, 2, -4,
            3, 38, 84, 24, -25,
            2, 13, 42, 13, 31,
            21, -4, 56, 46, -1,
            -1, 35, 79, -13, 19,
            -7, 65, 88, -9, -14,
            20, 4, 81, 49, -29,
            20, 0, 75, 3, -17,
            5, -9, 44, 92, -8,
            1, -3, 22, 69, 31,
            -6, 95, 41, -12, 5,
            39, 67, 16, -4, 1,
            0, -6, 120, 55, -36,
            -13, 44, 122, 4, -24,
            81, 5, 11, 3, 7,
            2, 0, 9, 10, 88,
        ],
    ];

    // ---------------------------------------------------------------------------------------
    // RFC 6716 §4.2.7.8.6, Table 53, and the NLSF quantization step sizes.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Excitation quantization offsets, Q10, indexed by signal type / 2 and quantization offset type
    /// (RFC 6716 §4.2.7.8.6, Table 53; 25 Q23 is 100 Q10, and so on).
    /// </summary>
    internal static readonly int[][] QuantizationOffsetQ10 =
    [
        [100, 240], // Inactive and unvoiced: low, high
        [32, 100],  // Voiced: low, high
    ];

    /// <summary>NLSF quantization step sizes, Q16, indexed by bandwidth NB, MB, WB.</summary>
    internal static readonly int[] NlsfQuantStepSizeQ16 = [11796, 11796, 9830];

    /// <summary>Order of the NLSF codebook, indexed by bandwidth NB, MB, WB.</summary>
    internal static readonly int[] NlsfOrder = [10, 10, 16];
}
