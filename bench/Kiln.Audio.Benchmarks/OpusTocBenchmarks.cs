using BenchmarkDotNet.Attributes;

namespace Kiln.Audio.Benchmarks;

/// <summary>Parses all 256 possible TOC bytes (RFC 6716 §3.1).</summary>
[MemoryDiagnoser]
public class OpusTocBenchmarks
{
    [Benchmark]
    public int ParseAllTocBytes()
    {
        int acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += OpusToc.Parse((byte)i).Config;
        }

        return acc;
    }
}
