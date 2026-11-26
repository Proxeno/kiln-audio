using System.Globalization;
using Kiln.Audio;

// Native AOT smoke test: encode and decode one second of 48 kHz mono audio through the
// public API, then check that the decoded signal still matches the input.

const int SampleRate = 48000;
const int FrameMilliseconds = 20;
const int FrameSamples = SampleRate * FrameMilliseconds / 1000;
const int FrameCount = 1000 / FrameMilliseconds;

// CELT output is delayed by the 2.5 ms overlap (120 samples at 48 kHz), so compare the
// decoded stream against the input shifted by that amount. The first four frames are the
// encoder's energy/MDCT warm-up, as in the codec's own round-trip tests.
const int StreamDelay = 120;
const int WarmupFrames = 4;

var encoderOptions = new OpusEncoderOptions
{
    Application = OpusApplication.RestrictedLowDelay,
    SampleRate = SampleRate,
    Channels = 1,
    FrameDuration = OpusFrameDuration.Ms20,
    BitrateBps = 64000,
    Complexity = 5,
};

using var encoder = new OpusEncoder(encoderOptions);
using var decoder = new OpusDecoder(new OpusDecoderOptions
{
    SampleRate = SampleRate,
    Channels = 1,
});

var input = new short[SampleRate];
for (int i = 0; i < input.Length; i++)
{
    double t = (double)i / SampleRate;
    input[i] = (short)Math.Round(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * t));
}

var packet = new byte[1500];
var decoded = new short[FrameSamples];
var roundTrip = new short[SampleRate];
long packetBytes = 0;

for (int frame = 0; frame < FrameCount; frame++)
{
    int written = encoder.Encode(
        input.AsSpan(frame * FrameSamples, FrameSamples),
        packet);
    packetBytes += written;

    int samples = decoder.Decode(packet.AsSpan(0, written), decoded);
    if (samples != FrameSamples)
    {
        Console.Error.WriteLine(FormattableString.Invariant(
            $"Frame {frame}: decoded {samples} samples, expected {FrameSamples}."));
        return 1;
    }

    decoded.CopyTo(roundTrip, frame * FrameSamples);
}

double signal = 0;
double noise = 0;
for (int i = (WarmupFrames * FrameSamples) + StreamDelay; i < input.Length; i++)
{
    double sample = input[i - StreamDelay];
    double error = sample - roundTrip[i];
    signal += sample * sample;
    noise += error * error;
}

double snrDb = 10 * Math.Log10(signal / Math.Max(noise, 1));
if (snrDb < 10)
{
    Console.Error.WriteLine(FormattableString.Invariant(
        $"Round-trip SNR is only {snrDb:F1} dB; the codec or its trim/AOT output is broken."));
    return 1;
}

Console.WriteLine(FormattableString.Invariant(
    $"AOT smoke OK: {FrameCount} packets, {packetBytes} bytes, {input.Length} samples, SNR {snrDb:F1} dB."));
return 0;
