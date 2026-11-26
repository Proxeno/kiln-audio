namespace Kiln.Audio.Celt;

/// <summary>
/// Every constant table used by the CELT decoder of RFC 6716 Section 4.3.
/// </summary>
/// <remarks>
/// <para>
/// The tables are transcribed from the RFC prose and tables. All indices are the ones the
/// RFC uses: band index <c>i</c> runs over the 21 critical bands, channel <c>c</c> runs over
/// the one or two stream channels, and <c>LM</c> is the base-2 logarithm of the number of
/// short MDCT blocks (0 for 2.5 ms up to 3 for 20 ms).
/// </para>
/// <para>
/// The values are fixed-point integers at the resolutions the RFC states; nothing in this
/// class is a floating-point approximation made by this implementation.
/// </para>
/// </remarks>
internal static class CeltTables
{
    /// <summary>Number of critical bands in the 48 kHz CELT mode.</summary>
    internal const int BandCount = 21;

    /// <summary>Number of samples in one short MDCT block (2.5 ms at 48 kHz).</summary>
    internal const int ShortBlockSamples = 120;

    /// <summary>Largest number of samples in one CELT frame (20 ms at 48 kHz).</summary>
    internal const int MaxFrameSamples = 8 * ShortBlockSamples;

    /// <summary>Resolution of the bit accounting, in bits per unit (RFC 6716 §4.1.6.2).</summary>
    internal const int BitRes = 3;

    /// <summary>Largest number of fine energy bits per band (RFC 6716 §4.3.2.2).</summary>
    internal const int MaxFineBits = 8;

    /// <summary>Number of pulse values representable by the allocation cache.</summary>
    internal const int MaxPseudo = 40;

    /// <summary>Number of bits in the base-2 index of the pulse cache.</summary>
    internal const int LogMaxPseudo = 6;

    /// <summary>Number of interleaved steps used by the allocation search (RFC 6716 §4.3.3).</summary>
    internal const int AllocSteps = 6;

    /// <summary>Number of allocation vectors (RFC 6716 §4.3.3, Table 56).</summary>
    internal const int AllocVectorCount = 11;

    /// <summary>
    /// Band edges in units of 2.5 ms MDCT bins, for the 48 kHz mode (RFC 6716 §4.3.1,
    /// Table 55). Band <c>i</c> spans <c>[eBands[i], eBands[i+1])</c> bins, and the values are
    /// scaled by the number of short blocks for the frame.
    /// </summary>
    internal static ReadOnlySpan<short> EBands =>
    [
        // RFC 6716 §4.3.1, Table 55.
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100,
    ];

    /// <summary>
    /// Base-2 logarithm of each band width, in 1/8 bit (RFC 6716 §4.3.3, Table 59).
    /// </summary>
    internal static ReadOnlySpan<short> LogN =>
    [
        // RFC 6716 §4.3.3, Table 59: log2_frac (band width, 3 fractional bits).
        0, 0, 0, 0, 0, 0, 0, 0, 8, 8, 8, 8, 16, 16, 16, 21, 21, 24, 29, 34, 36,
    ];

    /// <summary>
    /// Bit allocation vectors in units of 1/32 bit per sample (RFC 6716 §4.3.3, Table 56).
    /// </summary>
    /// <remarks>Eleven vectors of 21 entries each, indexed <c>vector * BandCount + band</c>.</remarks>
    internal static ReadOnlySpan<byte> BandAllocation =>
    [
        // RFC 6716 §4.3.3, Table 56.
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        90, 80, 75, 69, 63, 56, 49, 40, 34, 29, 20, 18, 10, 0, 0, 0, 0, 0, 0, 0, 0,
        110, 100, 90, 84, 78, 71, 65, 58, 51, 45, 39, 32, 26, 20, 12, 0, 0, 0, 0, 0, 0,
        118, 110, 103, 93, 86, 80, 75, 70, 65, 59, 53, 47, 40, 31, 23, 15, 4, 0, 0, 0, 0,
        126, 119, 112, 104, 95, 89, 83, 78, 72, 66, 60, 54, 47, 39, 32, 25, 17, 12, 1, 0, 0,
        134, 127, 120, 114, 103, 97, 91, 85, 78, 72, 66, 60, 54, 47, 41, 35, 29, 23, 16, 10, 1,
        144, 137, 130, 124, 113, 107, 101, 95, 88, 82, 76, 70, 64, 57, 51, 45, 39, 33, 26, 15, 1,
        152, 145, 138, 132, 123, 117, 111, 105, 98, 92, 86, 80, 74, 67, 61, 55, 49, 43, 36, 20, 1,
        162, 155, 148, 142, 133, 127, 121, 115, 108, 102, 96, 90, 84, 77, 71, 65, 59, 53, 46, 30, 1,
        172, 165, 158, 152, 143, 137, 131, 125, 118, 112, 106, 100, 94, 87, 81, 75, 69, 63, 56, 45, 20,
        200, 200, 200, 200, 200, 200, 200, 200, 198, 193, 188, 183, 178, 173, 168, 163, 158, 153, 148, 129, 104,
    ];

    /// <summary>
    /// Coarse-energy prediction coefficients, in Q15 (RFC 6716 §4.3.2.1, Table 57).
    /// </summary>
    /// <remarks>Indexed by <c>LM</c>; the values are 0.9, 0.8, 0.65 and 0.5.</remarks>
    internal static ReadOnlySpan<short> PredictionCoefficient => [29440, 26112, 21248, 16384];

    /// <summary>
    /// Intra-frame coarse-energy leakage coefficients, in Q15 (RFC 6716 §4.3.2.1, Table 57).
    /// </summary>
    internal static ReadOnlySpan<short> BetaCoefficient => [30147, 22282, 12124, 6554];

    /// <summary>Inter-band coarse-energy leak for intra frames, in Q15 (RFC 6716 §4.3.2.1, Table 57).</summary>
    internal const short BetaIntra = 4915;

    /// <summary>
    /// Coarse-energy Laplace probability model (RFC 6716 §4.3.2.1, Table 58).
    /// </summary>
    /// <remarks>
    /// <c>[LM][intra][2*band + 0]</c> is the probability of zero in Q8 and
    /// <c>[LM][intra][2*band + 1]</c> the decay rate in Q8. Only the first 21 bands are used;
    /// the table stores 21 pairs per (LM, intra) combination.
    /// </remarks>
    internal static ReadOnlySpan<byte> EnergyProbabilityModel =>
    [
        // RFC 6716 §4.3.2.1, Table 58: 120-sample frames, inter then intra.
        72, 127, 65, 129, 66, 128, 65, 128, 64, 128, 62, 128, 64, 128,
        64, 128, 92, 78, 92, 79, 92, 78, 90, 79, 116, 41, 115, 40,
        114, 40, 132, 26, 132, 26, 145, 17, 161, 12, 176, 10, 177, 11,
        24, 179, 48, 138, 54, 135, 54, 132, 53, 134, 56, 133, 55, 132,
        55, 132, 61, 114, 70, 96, 74, 88, 75, 88, 87, 74, 89, 66,
        91, 67, 100, 59, 108, 50, 120, 40, 122, 37, 97, 43, 78, 50,
        // 240-sample frames, inter then intra.
        83, 78, 84, 81, 88, 75, 86, 74, 87, 71, 90, 73, 93, 74,
        93, 74, 109, 40, 114, 36, 117, 34, 117, 34, 143, 17, 145, 18,
        146, 19, 162, 12, 165, 10, 178, 7, 189, 6, 190, 8, 177, 9,
        23, 178, 54, 115, 63, 102, 66, 98, 69, 99, 74, 89, 71, 91,
        73, 91, 78, 89, 86, 80, 92, 66, 93, 64, 102, 59, 103, 60,
        104, 60, 117, 52, 123, 44, 138, 35, 133, 31, 97, 38, 77, 45,
        // 480-sample frames, inter then intra.
        61, 90, 93, 60, 105, 42, 107, 41, 110, 45, 116, 38, 113, 38,
        112, 38, 124, 26, 132, 27, 136, 19, 140, 20, 155, 14, 159, 16,
        158, 18, 170, 13, 177, 10, 187, 8, 192, 6, 175, 9, 159, 10,
        21, 178, 59, 110, 71, 86, 75, 85, 84, 83, 91, 66, 88, 73,
        87, 72, 92, 75, 98, 72, 105, 58, 107, 54, 115, 52, 114, 55,
        112, 56, 129, 51, 132, 40, 150, 33, 140, 29, 98, 35, 77, 42,
        // 960-sample frames, inter then intra.
        42, 121, 96, 66, 108, 43, 111, 40, 117, 44, 123, 32, 120, 36,
        119, 33, 127, 33, 134, 34, 139, 21, 147, 23, 152, 20, 158, 25,
        154, 26, 166, 21, 173, 16, 184, 13, 184, 10, 150, 13, 139, 15,
        22, 178, 63, 114, 74, 82, 84, 83, 92, 82, 103, 62, 96, 72,
        96, 67, 101, 73, 107, 72, 113, 55, 118, 52, 125, 52, 118, 52,
        117, 55, 135, 49, 137, 39, 157, 32, 145, 29, 97, 33, 77, 40,
    ];

    /// <summary>
    /// Inverse CDF for the three-state coarse-energy fallback model, in 1/4 bit
    /// (RFC 6716 §4.3.2.1).
    /// </summary>
    internal static ReadOnlySpan<byte> SmallEnergyIcdf => [2, 1, 0];

    /// <summary>
    /// Pulse cache: the offset into <see cref="CacheBits"/> for each (LM, band)
    /// (RFC 6716 §4.3.3, Table 60). A value of -1 marks a band with no coefficients.
    /// </summary>
    internal static ReadOnlySpan<short> CacheIndex =>
    [
        // RFC 6716 §4.3.3, Table 60: 5 rows (LM -1..3) of 21 bands.
        -1, -1, -1, -1, -1, -1, -1, -1, 0, 0, 0, 0, 41, 41, 41,
        82, 82, 123, 164, 200, 222, 0, 0, 0, 0, 0, 0, 0, 0, 41,
        41, 41, 41, 123, 123, 123, 164, 164, 240, 266, 283, 295, 41, 41, 41,
        41, 41, 41, 41, 41, 123, 123, 123, 123, 240, 240, 240, 266, 266, 305,
        318, 328, 336, 123, 123, 123, 123, 123, 123, 123, 123, 240, 240, 240, 240,
        305, 305, 305, 318, 318, 343, 351, 358, 364, 240, 240, 240, 240, 240, 240,
        240, 240, 305, 305, 305, 305, 343, 343, 343, 351, 351, 370, 376, 382, 387,
    ];

    /// <summary>
    /// Pulse cache: bit cost of each pulse count minus one, prefixed by the largest pulse
    /// index (RFC 6716 §4.3.3, Table 61).
    /// </summary>
    internal static ReadOnlySpan<byte> CacheBits =>
    [
        // RFC 6716 §4.3.3, Table 61.
        40, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 40, 15, 23, 28,
        31, 34, 36, 38, 39, 41, 42, 43, 44, 45, 46, 47, 47, 49, 50,
        51, 52, 53, 54, 55, 55, 57, 58, 59, 60, 61, 62, 63, 63, 65,
        66, 67, 68, 69, 70, 71, 71, 40, 20, 33, 41, 48, 53, 57, 61,
        64, 66, 69, 71, 73, 75, 76, 78, 80, 82, 85, 87, 89, 91, 92,
        94, 96, 98, 101, 103, 105, 107, 108, 110, 112, 114, 117, 119, 121, 123,
        124, 126, 128, 40, 23, 39, 51, 60, 67, 73, 79, 83, 87, 91, 94,
        97, 100, 102, 105, 107, 111, 115, 118, 121, 124, 126, 129, 131, 135, 139,
        142, 145, 148, 150, 153, 155, 159, 163, 166, 169, 172, 174, 177, 179, 35,
        28, 49, 65, 78, 89, 99, 107, 114, 120, 126, 132, 136, 141, 145, 149,
        153, 159, 165, 171, 176, 180, 185, 189, 192, 199, 205, 211, 216, 220, 225,
        229, 232, 239, 245, 251, 21, 33, 58, 79, 97, 112, 125, 137, 148, 157,
        166, 174, 182, 189, 195, 201, 207, 217, 227, 235, 243, 251, 17, 35, 63,
        86, 106, 123, 139, 152, 165, 177, 187, 197, 206, 214, 222, 230, 237, 250,
        25, 31, 55, 75, 91, 105, 117, 128, 138, 146, 154, 161, 168, 174, 180,
        185, 190, 200, 208, 215, 222, 229, 235, 240, 245, 255, 16, 36, 65, 89,
        110, 128, 144, 159, 173, 185, 196, 207, 217, 226, 234, 242, 250, 11, 41,
        74, 103, 128, 151, 172, 191, 209, 225, 241, 255, 9, 43, 79, 110, 138,
        163, 186, 207, 227, 246, 12, 39, 71, 99, 123, 144, 164, 182, 198, 214,
        228, 241, 253, 9, 44, 81, 113, 142, 168, 192, 214, 235, 255, 7, 49,
        90, 127, 160, 191, 220, 247, 6, 51, 95, 134, 170, 203, 234, 7, 47,
        87, 123, 155, 184, 212, 237, 6, 52, 97, 137, 174, 208, 240, 5, 57,
        106, 151, 192, 231, 5, 59, 111, 158, 202, 243, 5, 55, 103, 147, 187,
        224, 5, 60, 113, 161, 206, 248, 4, 65, 122, 175, 224, 4, 67, 127,
        182, 234,
    ];

    /// <summary>
    /// Pulse cache: maximum useful bit count per band, minus 64
    /// (RFC 6716 §4.3.3, Table 62), indexed <c>21 * (2*LM + C - 1) + band</c>.
    /// </summary>
    internal static ReadOnlySpan<byte> CacheCaps =>
    [
        // RFC 6716 §4.3.3, Table 62.
        224, 224, 224, 224, 224, 224, 224, 224, 160, 160, 160, 160, 185, 185, 185,
        178, 178, 168, 134, 61, 37, 224, 224, 224, 224, 224, 224, 224, 224, 240,
        240, 240, 240, 207, 207, 207, 198, 198, 183, 144, 66, 40, 160, 160, 160,
        160, 160, 160, 160, 160, 185, 185, 185, 185, 193, 193, 193, 183, 183, 172,
        138, 64, 38, 240, 240, 240, 240, 240, 240, 240, 240, 207, 207, 207, 207,
        204, 204, 204, 193, 193, 180, 143, 66, 40, 185, 185, 185, 185, 185, 185,
        185, 185, 193, 193, 193, 193, 193, 193, 193, 183, 183, 172, 138, 65, 39,
        207, 207, 207, 207, 207, 207, 207, 207, 204, 204, 204, 204, 201, 201, 201,
        188, 188, 176, 141, 66, 40, 193, 193, 193, 193, 193, 193, 193, 193, 193,
        193, 193, 193, 194, 194, 194, 184, 184, 173, 139, 65, 39, 204, 204, 204,
        204, 204, 204, 204, 204, 201, 201, 201, 201, 198, 198, 198, 187, 187, 175,
        140, 66, 40,
    ];

    /// <summary>
    /// Time/frequency resolution change table (RFC 6716 §4.3.1, Table 63).
    /// </summary>
    /// <remarks>
    /// Row is <c>LM</c>; column is <c>4*isTransient + 2*tf_select + band_change</c>.
    /// Positive values mean more frequency resolution, negative values more time resolution.
    /// </remarks>
    internal static ReadOnlySpan<sbyte> TfSelectTable =>
    [
        // RFC 6716 §4.3.1, Table 63.
        0, -1, 0, -1, 0, -1, 0, -1,
        0, -1, 0, -2, 1, 0, 1, -1,
        0, -2, 0, -3, 2, 0, 1, -1,
        0, -2, 0, -3, 3, 0, 1, -1,
    ];

    /// <summary>Spread decision inverse CDF, 1/32 bit (RFC 6716 §4.3.4.3, Table 64).</summary>
    /// <remarks>The four states are none, light, normal and aggressive.</remarks>
    internal static ReadOnlySpan<byte> SpreadIcdf => [25, 23, 2, 0];

    /// <summary>Allocation trim inverse CDF, 1/128 bit (RFC 6716 §4.3.3, Table 65).</summary>
    internal static ReadOnlySpan<byte> TrimIcdf => [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];

    /// <summary>Post-filter tap-set inverse CDF, 1/4 bit (RFC 6716 §4.3.7.1, Table 66).</summary>
    internal static ReadOnlySpan<byte> TapsetIcdf => [2, 1, 0];

    /// <summary>Spread rotation factors (RFC 6716 §4.3.4.3), indexed by spread minus one.</summary>
    internal static ReadOnlySpan<int> SpreadFactor => [15, 10, 5];

    /// <summary>
    /// <c>exp2</c> lookup for the theta resolution (RFC 6716 §4.3.4.1), in Q14.
    /// </summary>
    internal static ReadOnlySpan<short> Exp2Table8 =>
    [
        16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048,
    ];

    /// <summary>
    /// 1/8-bit logarithm table used to reserve stereo side information
    /// (RFC 6716 §4.3.3, Table 67).
    /// </summary>
    internal static ReadOnlySpan<byte> Log2FracTable =>
    [
        0, 8, 13, 16, 19, 21, 23, 24, 26, 27, 28, 29, 30, 31, 32,
        32, 33, 34, 34, 35, 36, 36, 37, 37,
    ];

    /// <summary>
    /// Collapse-mask bit interleave table used when bands are recombined
    /// (RFC 6716 §4.3.4.4).
    /// </summary>
    internal static ReadOnlySpan<byte> BitInterleaveTable =>
    [
        0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3,
    ];

    /// <summary>
    /// Collapse-mask bit de-interleave table used when bands are recombined
    /// (RFC 6716 §4.3.4.4).
    /// </summary>
    internal static ReadOnlySpan<byte> BitDeinterleaveTable =>
    [
        0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F,
        0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF,
    ];

    /// <summary>
    /// Hadamard ordering table for the long-block transform (RFC 6716 §4.3.4.4); rows are
    /// for N=2, 4, 8 and 16.
    /// </summary>
    internal static ReadOnlySpan<int> OrderyTable =>
    [
        1, 0,
        3, 0, 2, 1,
        7, 0, 4, 3, 6, 1, 5, 2,
        15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
    ];

    /// <summary>Returns the pulse count represented by a cache index (RFC 6716 §4.3.3).</summary>
    /// <param name="i">The cache index, 0 to 40.</param>
    /// <returns>The number of pulses.</returns>
    internal static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    /// <summary>Returns <c>ilog(x)</c>: the number of bits needed to represent <paramref name="x"/>.</summary>
    /// <param name="x">A positive integer.</param>
    /// <returns>The bit length.</returns>
    internal static int Ilog(int x) => System.Numerics.BitOperations.Log2((uint)x) + 1;
}
