using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// G1: SILK-only packets decode in libopus with no range-coder mismatch, and our decoder
/// reproduces the encoder's final range on every packet.
/// </summary>
public class SilkEncoderInteropTests
{
    private static readonly Lazy<IReadOnlyList<(int Vector, short[] Mono)>> Corpus = new(LoadCorpus);

    public static TheoryData<OpusBandwidth, OpusFrameDuration, int> Combinations()
    {
        var data = new TheoryData<OpusBandwidth, OpusFrameDuration, int>();
        foreach (OpusBandwidth bandwidth in new[]
                 {
                     OpusBandwidth.Narrowband, OpusBandwidth.Mediumband, OpusBandwidth.Wideband,
                 })
        {
            foreach (OpusFrameDuration duration in new[]
                     {
                         OpusFrameDuration.Ms20, OpusFrameDuration.Ms60,
                     })
            {
                foreach (int bitrate in new[] { 8000, 12000, 20000 })
                {
                    if (bandwidth == OpusBandwidth.Narrowband && bitrate == 20000)
                    {
                        continue; // 8 kHz internal: 20 kbps is not a valid NB combination.
                    }

                    data.Add(bandwidth, duration, bitrate);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Silk_stream_decodes_in_libopus_and_our_decoder(OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int ms = duration == OpusFrameDuration.Ms20 ? 20 : 60;
        int frameSamples = 48000 * ms / 1000;

        foreach ((int vector, short[] mono) in Corpus.Value)
        {
            var packets = Encode(mono, bandwidth, duration, bitrate);

            using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
            var pcm = new short[frameSamples];
            foreach ((byte[] packet, uint expectedRange) in packets)
            {
                (packet[0] >> 3).Should().BeLessThan(12, $"vector {vector}: the TOC must be SILK-only");
                int decoded = decoder.Decode(packet, pcm);
                decoded.Should().Be(frameSamples);
                decoder.FinalRange.Should().Be(expectedRange, $"vector {vector}: final range must match");
            }

            string path = Path.Combine(Path.GetTempPath(), $"kiln-silk-g1-{Guid.NewGuid():N}.bit");
            try
            {
                WriteBitFile(path, packets);
                var result = RunOpusDemo(path);
                result.ExitCode.Should().Be(0, $"vector {vector}, {bandwidth}, {ms} ms, {bitrate} bps: {result.Diagnostics}");
                result.Diagnostics.Should().NotContain(
                    "mismatch", $"vector {vector}, {bandwidth}, {ms} ms, {bitrate} bps: {result.Diagnostics}");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static List<(byte[] Packet, uint Range)> Encode(
        short[] mono, OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = duration,
            Complexity = 5,
        };

        int frameSamples = 48000 * (duration == OpusFrameDuration.Ms20 ? 20 : 60) / 1000;
        using var encoder = new OpusEncoder(options);
        var packets = new List<(byte[], uint)>();
        var packet = new byte[1276];
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            int written = encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
            packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        return packets;
    }

    private static void WriteBitFile(string path, IReadOnlyList<(byte[] Packet, uint Range)> packets)
    {
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[8];
        foreach ((byte[] packet, uint range) in packets)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], packet.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[4..], range);
            stream.Write(header);
            stream.Write(packet);
        }
    }

    private static (int ExitCode, string Diagnostics) RunOpusDemo(string bitPath)
    {
        string output = Path.Combine(Path.GetTempPath(), $"kiln-silk-g1-{Guid.NewGuid():N}.pcm");
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(LibOpusOracle.Directory, OperatingSystem.IsWindows() ? "opus_demo.exe" : "opus_demo"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-d", "48000", "1", bitPath, output })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static IReadOnlyList<(int Vector, short[] Mono)> LoadCorpus()
    {
        // Speech vectors: 02 (NB), 03 (MB), 04 (WB) SILK, plus 06 (FB hybrid, still speech).
        var corpus = new List<(int, short[])>();
        foreach (int vector in new[] { 2, 3, 4, 6 })
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(vector));
            var mono = new short[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
            }

            corpus.Add((vector, mono));
        }

        return corpus;
    }
}
