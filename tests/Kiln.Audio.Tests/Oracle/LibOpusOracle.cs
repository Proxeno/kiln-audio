using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Black-box access to the libopus reference tools built by <c>scripts/build-libopus-oracle.sh</c>:
/// <c>opus_demo</c> decodes packets and <c>opus_compare</c> scores PCM. Test-only; nothing under
/// <c>src/</c> may reference libopus.
/// </summary>
internal static partial class LibOpusOracle
{
    private const string DirectoryVariable = "KILN_AUDIO_ORACLE_DIR";
    private const int ProcessTimeoutMilliseconds = 10 * 60 * 1000;

    private static readonly Lazy<string?> BinDirectory = new(LocateBinDirectory);

    /// <summary>True when both <c>opus_demo</c> and <c>opus_compare</c> are present.</summary>
    internal static bool Available => BinDirectory.Value is not null;

    /// <summary>Location of the oracle binaries.</summary>
    /// <exception cref="InvalidOperationException">The oracle is not available.</exception>
    internal static string Directory => BinDirectory.Value
        ?? throw new InvalidOperationException("The libopus oracle is not available.");

    /// <summary>Result of running <c>opus_compare</c>.</summary>
    /// <param name="Passed">True when the reference tool reports a pass.</param>
    /// <param name="Quality">Quality percentage derived from the printed internal weighted error.</param>
    /// <param name="Error">Internal weighted error as printed by <c>opus_compare</c>.</param>
    internal readonly record struct Comparison(bool Passed, double Quality, double Error);

    /// <summary>
    /// Decodes a length-prefixed Opus packet file with <c>opus_demo -d</c>.
    /// </summary>
    /// <param name="bitPath">Path to the <c>.bit</c> file.</param>
    /// <param name="sampleRate">Output sample rate.</param>
    /// <param name="channels">Output channels, 1 or 2.</param>
    /// <returns>Interleaved 16-bit PCM.</returns>
    /// <exception cref="InvalidOperationException"><c>opus_demo</c> failed.</exception>
    internal static short[] Decode(string bitPath, int sampleRate, int channels)
    {
        ArgumentException.ThrowIfNullOrEmpty(bitPath);
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 or 2.");
        }

        string outputPath = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-demo-{Guid.NewGuid():N}.pcm"));
        try
        {
            var arguments = new List<string>
            {
                "-d",
                sampleRate.ToString(CultureInfo.InvariantCulture),
                channels.ToString(CultureInfo.InvariantCulture),
                bitPath,
                outputPath,
            };
            var result = Run(Path.Combine(Directory, ExeName("opus_demo")), arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"opus_demo failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            return Pcm16.Read(outputPath);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    /// <summary>
    /// Runs <c>opus_compare</c> on two PCM buffers and parses its verdict.
    /// </summary>
    /// <remarks>
    /// The quality is recomputed from the printed internal weighted error rather than parsed from
    /// the "quality metric" line, because that line is printed with one decimal; the error is
    /// printed with six, which keeps the agreement check meaningful to 0.01 percentage points.
    /// </remarks>
    /// <param name="reference">Reference PCM, interleaved stereo.</param>
    /// <param name="test">Test PCM, interleaved with <paramref name="stereo"/> channel(s).</param>
    /// <param name="sampleRate">Comparison sample rate.</param>
    /// <param name="stereo">True when <paramref name="test"/> has two channels.</param>
    /// <returns>The parsed comparison.</returns>
    /// <exception cref="InvalidOperationException"><c>opus_compare</c> failed to run or printed no verdict.</exception>
    internal static Comparison Compare(short[] reference, short[] test, int sampleRate, bool stereo)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(test);

        string referencePath = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-ref-{Guid.NewGuid():N}.pcm"));
        string testPath = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-test-{Guid.NewGuid():N}.pcm"));
        try
        {
            Pcm16.Write(referencePath, reference);
            Pcm16.Write(testPath, test);

            var arguments = new List<string>();
            if (stereo)
            {
                arguments.Add("-s");
            }

            arguments.Add("-r");
            arguments.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            arguments.Add(referencePath);
            arguments.Add(testPath);

            var result = Run(Path.Combine(Directory, ExeName("opus_compare")), arguments);
            if (result.ExitCode is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"opus_compare failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            bool passed = result.StandardError.Contains("Test vector PASSES", StringComparison.Ordinal);
            if (!passed && !result.StandardError.Contains("Test vector FAILS", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"opus_compare printed no verdict: {result.StandardError}");
            }

            double error = ParseWeightedError(result.StandardError);
            double quality = 100 * (1 - (0.5 * Math.Log(1 + error) / Math.Log(1.13)));
            return new Comparison(passed, quality, error);
        }
        finally
        {
            if (File.Exists(referencePath))
            {
                File.Delete(referencePath);
            }

            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }
    }

    private static double ParseWeightedError(string stderr)
    {
        const string marker = "internal weighted error is ";
        int start = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new InvalidOperationException($"opus_compare printed no weighted error: {stderr}");
        }

        start += marker.Length;
        int end = start;
        while (end < stderr.Length && (char.IsAsciiDigit(stderr[end]) || stderr[end] is '.' or 'e' or 'E' or '+' or '-'))
        {
            end++;
        }

        if (end == start)
        {
            throw new InvalidOperationException($"Could not parse the weighted error: {stderr}");
        }

        return double.Parse(stderr[start..end], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(
        string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{fileName} timed out after {ProcessTimeoutMilliseconds} ms.");
        }

        process.WaitForExit();
        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? LocateBinDirectory()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return HasTools(fromEnvironment) ? Path.GetFullPath(fromEnvironment) : null;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".oracle", "bin");
            if (HasTools(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool HasTools(string directory) =>
        File.Exists(Path.Combine(directory, ExeName("opus_demo")))
        && File.Exists(Path.Combine(directory, ExeName("opus_compare")));
}
