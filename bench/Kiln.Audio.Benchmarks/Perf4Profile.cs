#if KILN_PERF4_PROFILE
using System.Diagnostics;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
#endif

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// PERF-4 stage profile of the CELT frequency-domain decoder.
/// </summary>
/// <remarks>
/// The decoder stamps each stage only when the library is compiled with
/// <c>KILN_PERF4_PROFILE</c>; a normal Release build folds the instrumentation away, so this
/// benchmark prints a message and does nothing when the symbol is absent. Build it with
/// <c>dotnet build -c Release -p:DefineConstants=KILN_PERF4_PROFILE</c> and run
/// <c>dotnet run -c Release --no-build -- project ... -- micro perf4</c>.
/// </remarks>
internal static class Perf4ProfileMeasure
{
#if KILN_PERF4_PROFILE
    private const int OutputChannels = 2;
#endif

#if !KILN_PERF4_PROFILE
    internal static void RunPublicSilk() =>
        Console.WriteLine("Profiling is compiled out; build with -p:DefineConstants=KILN_PERF4_PROFILE.");
#endif

    /// <summary>Runs the stage profile for the three CELT comparison vectors.</summary>
    public static void Run()
    {
#if !KILN_PERF4_PROFILE
        Console.WriteLine("PERF-4 profiling is compiled out; build with -p:DefineConstants=KILN_PERF4_PROFILE.");
        return;
#else
        Console.WriteLine("== PERF-4 CELT stage profile (us/frame, instrumented) ==");
        foreach (int vector in new[] { 1, 7, 8, 9, 11 })
        {
            Measure(vector);
        }

        Console.WriteLine();
        Console.WriteLine("== PERF-4 SILK synthesis stage profile (us/frame, instrumented) ==");
        foreach (int vector in new[] { 2, 3, 4 })
        {
            MeasureSilk(vector);
        }
#endif
    }

#if KILN_PERF4_PROFILE
    /// <summary>SIMD-2: per-stage profile of the public SILK/hybrid decode path (48 kHz).</summary>
    internal static void RunPublicSilk()
    {
        foreach (int channels in new[] { 2, 1 })
        {
            foreach (int vector in new[] { 2, 3, 4, 5, 6, 10, 12 })
            {
                byte[][] packets = TestVectorData.ReadPackets(vector);
                var options = new OpusDecoderOptions { SampleRate = 48000, Channels = channels };
                short[] pcm = new short[48000 * 120 / 1000 * channels];
                using (var warm = new OpusDecoder(options))
                {
                    for (int p = 0; p < 20; p++)
                    {
                        foreach (byte[] packet in packets)
                        {
                            warm.Decode(packet, pcm);
                        }
                    }
                }

                SilkPerf4Profile.Reset();
                const int Passes = 5;
                for (int p = 0; p < Passes; p++)
                {
                    using var decoder = new OpusDecoder(options);
                    foreach (byte[] packet in packets)
                    {
                        decoder.Decode(packet, pcm);
                    }
                }

                double us = 1_000_000.0 / Stopwatch.Frequency;
                double packetsDone = Math.Max(1, SilkPerf4Profile.Calls[27]);
                Console.WriteLine(FormattableString.Invariant(
                    $"-- vector {vector:D2} api-channels={channels}: us per packet (packets={packetsDone / Passes:F0}/pass)"));
                for (int slot = 0; slot < SilkPerf4Profile.SlotCount; slot++)
                {
                    if (SilkPerf4Profile.Calls[slot] == 0)
                    {
                        continue;
                    }

                    Console.WriteLine(FormattableString.Invariant(
                        $"  {SilkPerf4Profile.Names[slot],-13} {SilkPerf4Profile.Ticks[slot] * us / packetsDone,8:F3} us   {SilkPerf4Profile.Calls[slot] / packetsDone,6:F2} calls/pkt"));
                }
            }
        }
    }

    private static void MeasureSilk(int vector)
    {
        int rateHz = vector switch
        {
            2 => 8000,
            3 => 12000,
            _ => 16000,
        };
        byte[][] packets = TestVectorData.ReadPackets(vector);
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var synthesizer = new SilkSynthesis();
        var result = new SilkFrameResult();
        var pcm = new short[2 * 3 * 320];

        for (int pass = 0; pass < 24; pass++)
        {
            DecodeSilkPass(packets, frames, decoder, synthesizer, result, pcm, rateHz, vector);
        }

        SilkPerf4Profile.Reset();
        const int Passes = 3;
        long framesDecoded = 0;
        for (int pass = 0; pass < Passes; pass++)
        {
            framesDecoded += DecodeSilkPass(packets, frames, decoder, synthesizer, result, pcm, rateHz, vector);
        }

        double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        double perFrame = Math.Max(1, framesDecoded);
        Console.WriteLine(FormattableString.Invariant(
            $"-- vector {vector:D2}: {framesDecoded} frames over {Passes} passes"));
        for (int slot = 0; slot < SilkPerf4Profile.SlotCount; slot++)
        {
            long calls = SilkPerf4Profile.Calls[slot];
            if (calls == 0)
            {
                continue;
            }

            double microseconds = SilkPerf4Profile.Ticks[slot] * microsecondsPerTick / perFrame;
            Console.WriteLine(FormattableString.Invariant(
                $"  {SilkPerf4Profile.Names[slot],-12} {microseconds,8:F3} us   {calls / perFrame,7:F1} calls/frame"));
        }
    }

    private static long DecodeSilkPass(
        byte[][] packets,
        Range[] frames,
        SilkDecoder decoder,
        SilkSynthesis synthesizer,
        SilkFrameResult result,
        short[] pcm,
        int rateHz,
        int vector)
    {
        decoder.Reset();
        synthesizer.Reset();
        long frameTotal = 0;
        Span<Range> frameRanges = frames;
        foreach (byte[] packet in packets)
        {
            OpusStatus status = OpusPacket.TryParse(packet, frameRanges, out int frameCount, out OpusToc toc, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"vector {vector:D2}: packet parse failed with {status}."));
            }

            int durationMs = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                _ => 60,
            };
            int channels = toc.IsStereo ? 2 : 1;
            for (int i = 0; i < frameCount; i++)
            {
                var rangeDecoder = new RangeDecoder(packet.AsSpan(frames[i]));
                decoder.DecodeFrame(ref rangeDecoder, rateHz, channels, durationMs, result);
                synthesizer.Synthesize(result, pcm);
                frameTotal++;
            }
        }

        return frameTotal;
    }

    private static void Measure(int vector)
    {
        byte[][] packets = TestVectorData.ReadPackets(vector);
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new CeltDecoder(OutputChannels);
        var synthesizer = new CeltSynthesizer(OutputChannels);
        var frame = new CeltFrameResult();
        var floatPcm = new float[OutputChannels * CeltTables.MaxFrameSamples];

        // Warm the JIT and the shared tables before the timed passes; the profiler is cleared
        // afterwards so only the measured passes contribute.
        for (int pass = 0; pass < 24; pass++)
        {
            DecodePass(packets, frames, decoder, synthesizer, frame, floatPcm, vector);
        }

        Perf4Profile.Reset();
        const int Passes = 3;
        long framesDecoded = 0;
        for (int pass = 0; pass < Passes; pass++)
        {
            framesDecoded += DecodePass(packets, frames, decoder, synthesizer, frame, floatPcm, vector);
        }

        double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        double perFrame = Math.Max(1, framesDecoded);
        Console.WriteLine(FormattableString.Invariant(
            $"-- vector {vector:D2}: {framesDecoded} frames over {Passes} passes"));
        double total = 0;
        for (int slot = 0; slot < Perf4Profile.SlotCount; slot++)
        {
            double microseconds = Perf4Profile.Ticks[slot] * microsecondsPerTick / perFrame;
            long calls = Perf4Profile.Calls[slot];
            if (calls == 0)
            {
                continue;
            }

            Console.WriteLine(FormattableString.Invariant(
                $"  {Perf4Profile.Names[slot],-13} {microseconds,8:F3} us   {calls / perFrame,7:F1} calls/frame"));
            // Slot 6 is the interpolation half of the allocation stage and is already inside
            // slot 5, so it is reported but not added to the top-level total.
            if (slot is >= 1 and <= 8 && slot != 6)
            {
                total += microseconds;
            }
        }

        Console.WriteLine(FormattableString.Invariant($"  {"top-level sum",-13} {total,8:F3} us"));
    }

    private static long DecodePass(
        byte[][] packets,
        Range[] frames,
        CeltDecoder decoder,
        CeltSynthesizer synthesizer,
        CeltFrameResult frame,
        float[] floatPcm,
        int vector)
    {
        decoder.Reset();
        synthesizer.Reset();
        long frameTotal = 0;
        Span<Range> frameRanges = frames;
        foreach (byte[] packet in packets)
        {
            OpusStatus status = OpusPacket.TryParse(packet, frameRanges, out int frameCount, out OpusToc toc, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"vector {vector:D2}: packet parse failed with {status}."));
            }

            int lm = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms2_5 => 0,
                OpusFrameDuration.Ms5 => 1,
                OpusFrameDuration.Ms10 => 2,
                _ => 3,
            };
            int endBand = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband => 17,
                OpusBandwidth.Wideband => 17,
                OpusBandwidth.SuperWideband => 19,
                _ => 21,
            };
            int streamChannels = toc.IsStereo ? 2 : 1;

            for (int i = 0; i < frameCount; i++)
            {
                Range range = frames[i];
                int size = range.End.Value - range.Start.Value;
                int frameSamples = CeltTables.ShortBlockSamples << lm;
                if (size <= 1)
                {
                    synthesizer.DecodeLost(lm, startBand: 0, floatPcm[..(frameSamples * OutputChannels)]);
                }
                else
                {
                    var rangeDecoder = new RangeDecoder(packet.AsSpan(range));
                    decoder.DecodeFrame(ref rangeDecoder, size, lm, 0, endBand, streamChannels, frame);
                    frameSamples = frame.SampleCount;
                    synthesizer.Synthesize(frame, floatPcm[..(frameSamples * OutputChannels)]);
                }

                frameTotal++;
            }
        }

        return frameTotal;
    }
#endif
}
