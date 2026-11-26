using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>Encoder side of the test-only libopus oracle.</summary>
internal static partial class LibOpusOracle
{
    /// <summary>
    /// Encodes PCM with <c>opus_demo -e voip</c> and returns the length-prefixed packet file.
    /// </summary>
    /// <param name="pcm">Interleaved 16-bit PCM.</param>
    /// <param name="rate">Input sample rate in hertz.</param>
    /// <param name="channels">Channel count, 1 or 2.</param>
    /// <param name="bitrate">Target bitrate in bits per second.</param>
    /// <param name="extraArgs">Additional <c>opus_demo</c> options, such as <c>-bandwidth WB</c>.</param>
    /// <returns>The contents of the <c>.bit</c> file.</returns>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is empty or the channel count is invalid.</exception>
    /// <exception cref="InvalidOperationException"><c>opus_demo</c> failed.</exception>
    internal static byte[] Encode(
        short[] pcm, int rate, int channels, int bitrate, string[] extraArgs)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(extraArgs);
        if (pcm.Length == 0 || pcm.Length % channels != 0)
        {
            throw new ArgumentException("PCM must be non-empty and a whole number of frames.", nameof(pcm));
        }

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 or 2.");
        }

        string stem = Guid.NewGuid().ToString("N");
        string inputPath = Path.Combine(Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-in-{stem}.pcm"));
        string outputPath = Path.Combine(Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-enc-{stem}.bit"));
        try
        {
            Pcm16.Write(inputPath, pcm);
            var arguments = new List<string>
            {
                "-e",
                "voip",
                rate.ToString(CultureInfo.InvariantCulture),
                channels.ToString(CultureInfo.InvariantCulture),
                bitrate.ToString(CultureInfo.InvariantCulture),
            };
            arguments.AddRange(extraArgs);
            arguments.Add(inputPath);
            arguments.Add(outputPath);

            var result = Run(Path.Combine(Directory, ExeName("opus_demo")), arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"opus_demo failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            if (File.Exists(inputPath))
            {
                File.Delete(inputPath);
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
