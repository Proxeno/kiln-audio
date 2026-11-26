namespace Kiln.Audio.Fuzz;

/// <summary>
/// Deterministic xorshift128+ generator used by the fuzz harness and the seed-selecting
/// mutator.
/// </summary>
/// <remarks>
/// <see cref="Random"/> makes no cross-version stability promise, so a finding chased down by a
/// five-minute session must be reproducible later. This generator has a fixed algorithm and a
/// fixed seeding procedure: the same seed always produces the same sequence.
/// </remarks>
internal sealed class FuzzRng
{
    private ulong _s0;
    private ulong _s1;

    /// <summary>Creates a generator from <paramref name="seed"/>.</summary>
    /// <param name="seed">Any 64-bit value.</param>
    internal FuzzRng(ulong seed)
    {
        // SplitMix64 spreads the seed over the two state words; all-zero state is forbidden.
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        if ((_s0 | _s1) == 0)
        {
            _s1 = 0x9E3779B97F4A7C15UL;
        }
    }

    /// <summary>Returns the next 64 random bits.</summary>
    /// <returns>A uniformly distributed 64-bit value.</returns>
    internal ulong NextUInt64()
    {
        ulong x = _s0;
        ulong y = _s1;
        _s0 = y;
        x ^= x << 23;
        x ^= x >> 17;
        x ^= y ^ (y >> 26);
        _s1 = x;
        return x + y;
    }

    /// <summary>Returns a value in [0, <paramref name="maxExclusive"/>).</summary>
    /// <param name="maxExclusive">Exclusive upper bound; must be positive.</param>
    /// <returns>The random value.</returns>
    internal int Next(int maxExclusive) => (int)(NextUInt64() % (uint)maxExclusive);

    /// <summary>Returns a value in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Exclusive upper bound.</param>
    /// <returns>The random value.</returns>
    internal int Next(int min, int max) => min + Next(max - min);

    /// <summary>Returns true with probability <paramref name="numerator"/> / <paramref name="denominator"/>.</summary>
    /// <param name="numerator">Number of favorable outcomes.</param>
    /// <param name="denominator">Total outcomes.</param>
    /// <returns>The random decision.</returns>
    internal bool Chance(int numerator, int denominator) => Next(denominator) < numerator;

    /// <summary>Returns one element chosen uniformly from <paramref name="items"/>.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="items">Non-empty collection.</param>
    /// <returns>The chosen element.</returns>
    internal T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    private static ulong SplitMix(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
