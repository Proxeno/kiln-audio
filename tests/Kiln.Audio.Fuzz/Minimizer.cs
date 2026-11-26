namespace Kiln.Audio.Fuzz;

/// <summary>Shrinks a failing input while the failure keeps reproducing.</summary>
internal static class Minimizer
{
    private const int MaxEvaluations = 4000;

    /// <summary>
    /// Removes progressively smaller chunks of <paramref name="input"/>, keeping a candidate only
    /// when <paramref name="fails"/> still reports the original failure.
    /// </summary>
    /// <param name="input">The failing input.</param>
    /// <param name="fails">Predicate that returns true when a candidate reproduces the failure.</param>
    /// <param name="maxEvaluations">Evaluation cap; the default suits a cheap predicate.</param>
    /// <returns>The smallest candidate found, or the input when nothing smaller reproduces.</returns>
    internal static byte[] Minimize(byte[] input, Func<byte[], bool> fails, int maxEvaluations = MaxEvaluations)
    {
        if (input.Length == 0 || !fails(input))
        {
            return input;
        }

        int evaluations = 1;
        byte[] current = input;
        int granularity = 2;
        while (current.Length > 0 && evaluations < maxEvaluations)
        {
            int chunk = Math.Max(1, current.Length / granularity);
            bool reduced = false;
            for (int start = 0; start < current.Length; start += chunk)
            {
                int length = Math.Min(chunk, current.Length - start);
                var candidate = new byte[current.Length - length];
                current.AsSpan(0, start).CopyTo(candidate);
                current.AsSpan(start + length).CopyTo(candidate.AsSpan(start));

                evaluations++;
                if (fails(candidate))
                {
                    current = candidate;
                    reduced = true;
                    granularity = Math.Max(2, granularity - 1);
                    break;
                }

                if (evaluations >= maxEvaluations)
                {
                    break;
                }
            }

            if (!reduced)
            {
                if (granularity >= current.Length)
                {
                    break;
                }

                granularity = Math.Min(current.Length, granularity * 2);
            }
        }

        return current;
    }
}
