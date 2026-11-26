using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Drives <c>ec_probe</c>, the test-only binary that exposes libopus's range coder over stdin.
/// </summary>
/// <remarks>
/// The probe format is documented in <c>tests/Kiln.Audio.Tests/Oracle/native/ec_probe.c</c>.
/// Encoder sessions print a <c>state</c> line per command and a final <c>bytes</c> line; decoder
/// sessions print one <c>value</c> line per command.
/// </remarks>
internal static partial class LibOpusOracle
{
    private const string ProbeExecutable = "ec_probe";

    /// <summary>True when the <c>ec_probe</c> binary is present next to the other oracle tools.</summary>
    internal static bool RangeProbeAvailable => BinDirectory.Value is not null
        && File.Exists(Path.Combine(BinDirectory.Value, ExeName(ProbeExecutable)));

    /// <summary>One probe observation: the decoded value (decoder) or zero (encoder), plus state.</summary>
    /// <param name="Value">The value decoded in a decoder session; zero in an encoder session.</param>
    /// <param name="Tell">Whole bits used (RFC 6716 §4.1.6.1).</param>
    /// <param name="TellFrac">Bits used in eighths (RFC 6716 §4.1.6.2).</param>
    /// <param name="Rng">The current range size.</param>
    /// <param name="Error">True when the reference reported an error.</param>
    internal readonly record struct RangeProbeStep(uint Value, int Tell, uint TellFrac, uint Rng, bool Error);

    /// <summary>The parsed output of one probe session.</summary>
    internal sealed class RangeProbeResult
    {
        /// <summary>One entry per command, in order.</summary>
        internal List<RangeProbeStep> Steps { get; } = [];

        /// <summary>The encoder's output frame, empty for decoder sessions.</summary>
        internal byte[] Bytes { get; set; } = [];
    }

    /// <summary>Runs one probe session and parses its output.</summary>
    /// <param name="script">The complete stdin script, one command per line.</param>
    /// <returns>The decoded values or states, and the encoder bytes when the session encoded.</returns>
    /// <exception cref="InvalidOperationException">The probe is unavailable or exited abnormally.</exception>
    internal static RangeProbeResult RangeProbe(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var startInfo = new ProcessStartInfo(Path.Combine(Directory, ExeName(ProbeExecutable)))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ec_probe.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"ec_probe timed out after {ProcessTimeoutMilliseconds} ms.");
        }

        process.WaitForExit();
        string output = standardOutput.GetAwaiter().GetResult();
        string error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ec_probe failed with exit code {process.ExitCode}: {error}");
        }

        return Parse(output);
    }

    private static RangeProbeResult Parse(string output)
    {
        var result = new RangeProbeResult();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (fields[0])
            {
                case "state":
                {
                    // state <tell> <tellfrac> <rng> <error>
                    RequireFieldCount(fields, 5, line);
                    result.Steps.Add(new RangeProbeStep(
                        Value: 0,
                        Tell: ParseInt(fields[1]),
                        TellFrac: ParseUInt(fields[2]),
                        Rng: ParseUInt(fields[3]),
                        Error: ParseInt(fields[4]) != 0));
                    break;
                }

                case "value":
                {
                    // value <v> <tell> <tellfrac> <rng> <error>
                    RequireFieldCount(fields, 6, line);
                    result.Steps.Add(new RangeProbeStep(
                        Value: ParseUInt(fields[1]),
                        Tell: ParseInt(fields[2]),
                        TellFrac: ParseUInt(fields[3]),
                        Rng: ParseUInt(fields[4]),
                        Error: ParseInt(fields[5]) != 0));
                    break;
                }

                case "bytes":
                {
                    // bytes <hex>
                    RequireFieldCount(fields, 2, line);
                    result.Bytes = Convert.FromHexString(fields[1]);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unexpected ec_probe output line: {line}");
            }
        }

        return result;
    }

    private static void RequireFieldCount(string[] fields, int expected, string line)
    {
        if (fields.Length != expected)
        {
            throw new InvalidOperationException($"Malformed ec_probe output line: {line}");
        }
    }

    private static int ParseInt(string field) =>
        int.Parse(field, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static uint ParseUInt(string field) =>
        uint.Parse(field, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
