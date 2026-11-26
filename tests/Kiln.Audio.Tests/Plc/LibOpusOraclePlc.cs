using System;
using System.Collections.Generic;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>Oracle decoding options the PLC and FEC tests need (loss and in-band FEC).</summary>
internal static partial class LibOpusOracle
{
    /// <summary>
    /// Decodes a length-prefixed packet file with <c>opus_demo -d</c>, passing extra decoder
    /// options such as <c>-inbandfec</c>.
    /// </summary>
    /// <param name="bitFile">Contents of the <c>.bit</c> file.</param>
    /// <param name="sampleRate">Output sample rate.</param>
    /// <param name="channels">Output channels, 1 or 2.</param>
    /// <param name="extraArgs">Additional options placed before the input path.</param>
    /// <returns>Interleaved 16-bit PCM.</returns>
    internal static short[] Decode(byte[] bitFile, int sampleRate, int channels, params string[] extraArgs)
    {
        ArgumentNullException.ThrowIfNull(bitFile);
        ArgumentNullException.ThrowIfNull(extraArgs);

        string bitPath = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-opt-{Guid.NewGuid():N}.bit"));
        string outputPath = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-opus-opt-{Guid.NewGuid():N}.pcm"));
        try
        {
            File.WriteAllBytes(bitPath, bitFile);
            var arguments = new List<string>
            {
                "-d",
                sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            arguments.AddRange(extraArgs);
            arguments.Add(bitPath);
            arguments.Add(outputPath);

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
            if (File.Exists(bitPath))
            {
                File.Delete(bitPath);
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
