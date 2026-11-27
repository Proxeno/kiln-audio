using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// SIMD-2: the inverse-CDF fast path of the SILK excitation decode must be bit-exact with the
/// forward-table <c>DecodePdf</c> it replaces, in symbol and in range-coder state.
/// </summary>
public class SilkIcdfDecodeTests
{
    [Fact]
    public void Icdf_decode_matches_forward_pdf_decode_for_every_excitation_table()
    {
        var tables = new List<byte[]>();
        tables.AddRange(SilkTables.PulseCountPdf);
        tables.AddRange(SilkTables.PulseSplit16Pdf);
        tables.AddRange(SilkTables.PulseSplit8Pdf);
        tables.AddRange(SilkTables.PulseSplit4Pdf);
        tables.AddRange(SilkTables.PulseSplit2Pdf);
        tables.Add(SilkTables.LsbPdf);
        foreach (byte[][][] offsets in SilkTables.SignPdf)
        {
            foreach (byte[][] rows in offsets)
            {
                tables.AddRange(rows);
            }
        }

        var rng = new Random(42);
        foreach (byte[] pdf in tables)
        {
            byte[] icdf = SilkTables.IcdfOf(pdf);
            Assert.Equal(0, icdf[^1]);
            for (int stream = 0; stream < 8; stream++)
            {
                var data = new byte[200];
                rng.NextBytes(data);
                var a = new RangeDecoder(data);
                var b = new RangeDecoder(data);
                for (int i = 0; i < 120; i++)
                {
                    int expected = a.DecodePdf(pdf);
                    int actual = b.DecodeIcdf8(icdf);
                    Assert.Equal(expected, actual);
                    Assert.Equal(a.Rng, b.Rng);
                    Assert.Equal(a.Error, b.Error);
                }
            }
        }
    }
}
