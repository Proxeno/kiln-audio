using System.Globalization;
using System.IO;

namespace Kiln.Audio.Fuzz;

/// <summary>
/// Reads and writes decoder sequences: the packets and loss calls that, replayed on one
/// decoder, reproduce a state-dependent finding.
/// </summary>
/// <remarks>
/// The format is a text header and one line per operation:
/// <c>step &lt;rate&gt; &lt;channels&gt; &lt;fec 0|1&gt; &lt;float 0|1&gt; &lt;loss 0|1&gt; &lt;packet hex or -&gt;</c>.
/// It is committed next to the regression test that replays it.
/// </remarks>
internal static class FuzzSequenceFile
{
    private const string Header = "FUZZ-SEQUENCE 1";

    /// <summary>Writes <paramref name="steps"/> to <paramref name="path"/>.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="steps">The steps, oldest first.</param>
    internal static void Write(string path, IReadOnlyList<FuzzStep> steps)
    {
        var lines = new List<string>(steps.Count + 1) { Header };
        foreach (FuzzStep step in steps)
        {
            string hex = step.Loss ? "-" : Convert.ToHexString(step.Packet).ToLowerInvariant();
            lines.Add(FormattableString.Invariant(
                $"step {step.SampleRate} {step.Channels} {(step.DecodeFec ? 1 : 0)} {(step.UseFloat ? 1 : 0)} {(step.Loss ? 1 : 0)} {hex}"));
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>Reads a sequence file.</summary>
    /// <param name="path">Source file.</param>
    /// <returns>The steps, oldest first.</returns>
    /// <exception cref="InvalidDataException">The file is not a valid sequence.</exception>
    internal static IReadOnlyList<FuzzStep> Read(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0].Trim() != Header)
        {
            throw new InvalidDataException(FormattableString.Invariant($"'{path}' is not a fuzz sequence file."));
        }

        var steps = new List<FuzzStep>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            string[] fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 7 || fields[0] != "step")
            {
                throw new InvalidDataException(FormattableString.Invariant($"Malformed sequence line {i + 1} in '{path}'."));
            }

            byte[] packet = fields[6] == "-" ? [] : Convert.FromHexString(fields[6]);
            steps.Add(
                new FuzzStep(
                    packet,
                    int.Parse(fields[1], CultureInfo.InvariantCulture),
                    int.Parse(fields[2], CultureInfo.InvariantCulture),
                    fields[3] == "1",
                    fields[4] == "1",
                    fields[5] == "1"));
        }

        return steps;
    }
}
