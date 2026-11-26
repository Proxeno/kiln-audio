using System.Diagnostics;
using System.Globalization;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>Drives the test-only libopus <c>opus_demo</c> for the encoder gates.</summary>
internal static class EncoderOracle
{
    /// <summary>Encodes mono PCM with <c>opus_demo -e restricted-lowdelay</c> and returns the <c>.bit</c> bytes.</summary>
    internal static byte[] EncodeRestrictedLowDelay(short[] mono, int bitrate, int frameMilliseconds, int complexity)
    {
        string input = Path.Combine(Path.GetTempPath(), $"kiln-enc-{Guid.NewGuid():N}.pcm");
        string output = Path.Combine(Path.GetTempPath(), $"kiln-enc-{Guid.NewGuid():N}.bit");
        try
        {
            Pcm16.Write(input, mono);
            var result = Run("opus_demo", new[]
            {
                "-e", "restricted-lowdelay", "48000", "1",
                bitrate.ToString(CultureInfo.InvariantCulture),
                "-framesize", frameMilliseconds.ToString(CultureInfo.InvariantCulture),
                "-cbr",
                "-complexity", complexity.ToString(CultureInfo.InvariantCulture),
                input, output,
            });
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"opus_demo encode failed ({result.ExitCode}): {result.StandardError}");
            }

            return File.ReadAllBytes(output);
        }
        finally
        {
            Delete(input);
            Delete(output);
        }
    }

    /// <summary>Decodes a <c>.bit</c> stream with <c>opus_demo -d 48000 1</c>.</summary>
    /// <returns>The decoded mono PCM and the raw diagnostics.</returns>
    internal static (short[] Pcm, string Diagnostics) Decode(string bitPath)
    {
        string output = Path.Combine(Path.GetTempPath(), $"kiln-dec-{Guid.NewGuid():N}.pcm");
        try
        {
            var result = Run("opus_demo", new[]
            {
                "-d", "48000", "1", bitPath, output,
            });
            string diagnostics = result.StandardOutput + result.StandardError;
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"opus_demo decode failed ({result.ExitCode}): {diagnostics}");
            }

            return (Pcm16.Read(output), diagnostics);
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>Decodes a <c>.bit</c> byte array with <c>opus_demo</c>.</summary>
    internal static (short[] Pcm, string Diagnostics) Decode(byte[] bitFile)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kiln-dec-{Guid.NewGuid():N}.bit");
        try
        {
            File.WriteAllBytes(path, bitFile);
            return Decode(path);
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>True when <c>opus_demo</c> reported a range-coder mismatch.</summary>
    internal static bool HasRangeMismatch(string diagnostics) =>
        diagnostics.Contains("mismatch", StringComparison.OrdinalIgnoreCase);

    private static (int ExitCode, string StandardOutput, string StandardError) Run(
        string tool, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(LibOpusOracle.Directory, ExeName(tool)))
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
            ?? throw new InvalidOperationException($"Could not start {tool}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10 * 60 * 1000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{tool} timed out.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
