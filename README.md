![Kiln.Audio](https://raw.githubusercontent.com/Proxeno/kiln-audio/main/docs/assets/hero.jpg)

# Kiln.Audio

[![CI](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml/badge.svg)](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Proxeno.Kiln.Audio)](https://www.nuget.org/packages/Proxeno.Kiln.Audio)
[![NuGet downloads](https://img.shields.io/nuget/dt/Proxeno.Kiln.Audio)](https://www.nuget.org/packages/Proxeno.Kiln.Audio)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-e8912d)](https://github.com/Proxeno/kiln-audio/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-e8912d)](https://dotnet.microsoft.com)

**An Opus encoder and decoder for .NET, written entirely in C#.** Pure managed, SIMD-accelerated,
zero native dependencies, Native AOT compatible, Apache-2.0. Kiln.Audio implements Opus
(RFC 6716, as updated by RFC 8251) inside this repository: the range coder, SILK, CELT, the hybrid
mode, packet loss concealment and in-band FEC. There is no native codec to cross-compile, ship or
keep patched. It is the audio sibling of [Kiln](https://github.com/Proxeno/kiln), the H.264
encoder.

> **Status: 1.0.** The decoder passes all 12 RFC 8251 conformance vectors through the public API.
> The encoder produces streams that libopus decodes with a matching final range on every packet.
> Every capability listed here is backed by a test in this repository, and
> [What Kiln.Audio is not](#what-kilnaudio-is-not) says plainly what is missing.

## What you can build

Kiln.Audio is the codec step of a real-time voice or audio pipeline, in one .NET process with no
native runtime on the box:

- **Voice and video calls.** Encode microphone audio to Opus and decode the far end, as the audio
  codec of a WebRTC or RTP stack such as [Keryx](https://www.nuget.org/packages/Proxeno.Keryx).
- **Voice agents and speech pipelines.** Decode incoming Opus to PCM for a recognizer and encode
  synthesized speech back, with no allocation per packet.
- **Game and application voice chat**, including Native AOT and trimmed deployments.
- **Anywhere** a managed process needs Opus without bundling `libopus` for every OS and CPU.

## Installing

The package id is `Proxeno.Kiln.Audio`. The assembly and namespace are `Kiln.Audio`, so code uses
`using Kiln.Audio;`.

```
dotnet add package Proxeno.Kiln.Audio
```

Requires .NET 10 or later. The package has no dependencies.

## Quick start

Encode and decode one frame:

```csharp
using Kiln.Audio;

using var encoder = new OpusEncoder(new OpusEncoderOptions
{
    SampleRate = 48000,
    Channels = 1,
    Application = OpusApplication.Voip,
    BitrateBps = 24000,
    FrameDuration = OpusFrameDuration.Ms20,
});
using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });

const int FrameSamples = 960;        // 20 ms at 48 kHz, per channel
var packet = new byte[1276];         // large enough for any single Opus frame
var decoded = new short[FrameSamples];

// pcm: exactly one frame of interleaved 16-bit samples.
int bytes = encoder.Encode(pcm, packet);
int samples = decoder.Decode(packet.AsSpan(0, bytes), decoded);
```

Handle a lost packet, using in-band FEC when the next packet carries it:

```csharp
using var encoder = new OpusEncoder(new OpusEncoderOptions
{
    Application = OpusApplication.Voip,
    BitrateBps = 32000,
    UseInbandFec = true,       // carry a low-bitrate copy of the previous frame
    PacketLossPercent = 10,    // tell the encoder how lossy the path is
});
using var decoder = new OpusDecoder(new OpusDecoderOptions());
var decoded = new short[960];

// The packet before `next` was lost.
if (OpusPacket.HasLbrr(next))
{
    decoder.Decode(next, decoded, decodeFec: true);   // recover the lost frame from `next`
}
else
{
    decoder.DecodeLoss(decoded);                      // no FEC data: conceal instead
}

decoder.Decode(next, decoded);                        // then decode `next` itself
```

Both snippets are compiled and run by the test suite
(`tests/Kiln.Audio.Tests/ReadmeQuickStartTests.cs`). `Encode` and `Decode` also have `float`
overloads for normalized samples in [-1, 1].

An encoder or decoder instance holds the state of one stream and is not thread-safe. Separate
instances are independent and can run on different threads at the same time.

## Features

| Area | Decoder | Encoder |
|---|---|---|
| SILK (speech, NB/MB/WB) | Yes, mono and stereo, 10/20/40/60 ms | Yes, mono and stereo, 10/20/40/60 ms |
| CELT (music and low delay, NB to FB) | Yes, mono and stereo, 2.5/5/10/20 ms | Yes, mono and stereo, 2.5 to 20 ms; 40/60 ms as packed 20 ms frames; CBR, VBR and constrained VBR |
| Hybrid (SILK + CELT, SWB/FB) | Yes, 10/20 ms | Yes, mono, 48 kHz input, 10/20 ms |
| Automatic mode selection | Follows the stream, with RFC 8251 transition redundancy | Per-frame SILK / hybrid / CELT choice for `Voip` and `Audio`, mono, 48 kHz, 10/20 ms |
| In-band FEC (LBRR) | Yes: `Decode(..., decodeFec: true)`, `OpusPacket.HasLbrr`, `LastFecOutcome` | Yes on SILK and hybrid frames (`UseInbandFec` with `PacketLossPercent`) |
| DTX | Decodes DTX packets | Yes on the SILK-only path (`UseDtx`) |
| Packet loss concealment | SILK, CELT and hybrid (`DecodeLoss`, or an empty packet) | n/a |
| Sample rates | 8, 12, 16, 24, 48 kHz output | 8, 12, 16, 24, 48 kHz input |
| Channels | 1 or 2 (mono/stereo up- and down-mix) | 1 or 2 |
| Performance modes | `PerformanceMode`: `Precise` (default, bit-identical to 1.0) or `Relaxed` (non-bit-exact float math) | `PerformanceMode`: `Precise` (default; matches 1.0 except CBR packets, whose low-rate fill and nominal sizing were corrected in 1.1.0) or `Relaxed` (cheaper SILK rate loop and quantizer, fewer CELT analysis passes; standard packets, small quality cost) |
| Packet layouts | Codes 0 to 3, padding, up to 120 ms per packet | One packet per `Encode` call |
| Sample formats | `short` and `float` | `short` and `float` |

Encoder options, all on `OpusEncoderOptions`:

| Option | Default | What it does |
|---|---|---|
| `SampleRate` | 48000 | Input rate: 8000, 12000, 16000, 24000 or 48000 Hz. |
| `Channels` | 1 | 1 or 2, interleaved. |
| `Application` | `Voip` | `Voip`, `Audio` or `RestrictedLowDelay` (always CELT). |
| `BitrateBps` | 24000 | Target bitrate. |
| `FrameDuration` | `Ms20` | 2.5, 5, 10, 20, 40 or 60 ms. |
| `Complexity` | 5 | 0 (fastest) to 10 (best quality). |
| `Bandwidth` | null | Caps the audio bandwidth, or selects the SILK bandwidth on the SILK-only path. |
| `UseVbr` / `ConstrainedVbr` | false / true | Variable bitrate for the CELT encoder. |
| `UseCbr` | false | Constant packet size for the SILK layer. |
| `PerformanceMode` | `Precise` | `Relaxed` encodes faster at a small quality cost; see [Performance modes](docs/performance.md#performance-modes). |
| `UseInbandFec` / `PacketLossPercent` | false / 0 | In-band FEC on SILK and hybrid frames. |
| `UseDtx` | false | Discontinuous transmission on the SILK-only path. |

Which encoder path the options select (automatic, SILK-only or CELT-only) is described in
[docs/architecture.md](docs/architecture.md#encoder-mode-selection) and in the `OpusEncoder` XML
documentation.

## Conformance and verification

- **Decoder conformance.** All 12 RFC 8251 test vectors (20,075 packets) pass the RFC's
  `opus_compare` quality metric through the public `OpusDecoder`, and the decoder's final range
  matches the reference value for every packet. See [docs/test-vectors.md](docs/test-vectors.md).
- **Encoder interoperability.** The streams the test suite encodes are decoded by libopus 1.5.2,
  used as a black-box oracle, with no range mismatch.
- **Encoder quality gates.** Segmental SNR must stay at or above frozen baselines of the RFC 6716
  Appendix A reference encoder, for SILK, stereo SILK and hybrid, and a generated full-band test
  signal checks that a fullband stream keeps the 13 to 19 kHz band. The gap to libopus 1.5.2 is
  measured and reported in [docs/performance.md](docs/performance.md); it is not gated.
- **SIMD parity.** There are four kernel sets: AdvSimd on arm64, AVX2+FMA on x64, a portable
  128-bit set, and scalar. Each vector set is tested against scalar: the integer SILK kernels are
  bit-identical, and the floating-point CELT kernels agree within a tight bound. CI runs the suite
  on Linux x64, Windows x64 and macOS arm64. Setting `KILN_AUDIO_DISABLE_SIMD=1` forces scalar.
- **Allocation.** `Encode`, `Decode` and `DecodeLoss` allocate nothing after construction; tests
  assert zero bytes.
- **Native AOT.** The library is built with the trim, AOT and single-file analyzers on and no
  warnings, and `tests/Kiln.Audio.AotSmoke` round-trips audio in a natively compiled binary.
- **Robustness.** The decoder accepts any byte string. A malformed packet throws
  `InvalidDataException` and leaves the decoder state unchanged; no other exception type escapes
  for packet content. A mutation fuzzer over the packet parser, the decoder (all rates, mono and
  stereo, FEC and loss) and the encoder ran 163 million executions across 17 sessions. It found
  two defects, both in packet loss concealment; both are fixed and have regression tests, and the
  150 million executions after the fixes found nothing. The fuzzer is coverage-blind, so this is
  evidence, not proof. See [docs/robustness.md](docs/robustness.md).

## Performance

Measured against libopus 1.5.2, built with its own NEON code enabled, in process on both sides, on
one Apple M5 Max (arm64). Ratios are Kiln time divided by libopus time, so above 1.0 means Kiln is
slower. The machine is shared, so differences under about 10 to 15 % are noise. The full tables,
method and history are in [docs/performance.md](docs/performance.md).

**Decoding is at parity with libopus.** Over the 12 RFC 8251 vectors through the public API at
48 kHz, the ratio is 0.85 to 1.06 with stereo output and 0.94 to 1.12 with mono output. CELT and
hybrid streams are mostly a little faster than libopus; SILK-only streams are 2 to 9 % slower.

**Encoding is far faster than real time, but slower than libopus at low complexity.** Time per
20 ms frame, constant bitrate, libopus pinned to the same mode and complexity:

| Configuration | Complexity | libopus (µs) | Kiln (µs) | Ratio |
|---|---|---|---|---|
| CELT mono, 32 kbit/s | 0 | 9.5 | 12.7 | 1.34 |
| CELT mono, 32 kbit/s | 5 | 34.4 | 25.7 | 0.75 |
| CELT stereo, 64 kbit/s | 0 | 18.5 | 25.3 | 1.37 |
| CELT stereo, 64 kbit/s | 5 | 47.2 | 40.2 | 0.85 |
| SILK wideband mono, 20 kbit/s | 0 | 44.5 | 103.0 | 2.31 |
| SILK wideband mono, 20 kbit/s | 5 | 135.8 | 121.6 | 0.90 |
| Hybrid fullband mono, 32 kbit/s | 0 | 31.7 | 101.2 | 3.19 |
| Hybrid fullband mono, 32 kbit/s | 5 | 70.0 | 131.7 | 1.88 |

The slowest row is 132 µs for a 20 ms frame. The CELT rows and the SILK/hybrid rows were measured
on two branches that have since been merged; the hybrid figure with both sets of changes has not
been re-measured. The low-complexity gap has a known cause: Kiln's SILK
encoder runs the delayed-decision quantizer at every complexity, where libopus uses a cheaper
greedy quantizer at complexity 0. So complexity 0 in Kiln buys less speed than it does in libopus.

**Encoder quality** (segmental SNR at complexity 0, higher is better): SILK meets or beats libopus
in 14 of 16 gated configurations and trails by 0.13 to 0.19 dB in the other two (mediumband at
20 kbit/s). At complexity 5 and 10, SILK trails libopus by up to 0.6 dB. CELT-only is about 1 dB
ahead of libopus at complexity 0 and within 0.35 dB at complexity 10. These are objective
waveform measurements on the RFC test vectors, not listening tests.

**x64:** one run on an AMD Ryzen 7 7700X (rented VM) has decode at 0.76 to 1.09 times libopus and
encode at complexity 0 slower than libopus in every configuration (1.55 to 3.37 times). It is a
single machine; see [docs/performance.md](docs/performance.md#x64-vast-ai-amd-ryzen-7-7700x).

## What Kiln.Audio is not

- **Not a drop-in for the libopus C API.** There are no `opus_encoder_ctl` requests. Options are
  fixed when the encoder is constructed; to change bitrate or mode, create a new encoder.
- **Hybrid and automatic mode selection are mono, 48 kHz, 10/20 ms only.** A stereo `Audio`
  encoder, or one at another input rate, uses CELT for every frame. A stereo `Voip` encoder uses
  SILK. Both produce valid Opus; they do not switch modes per frame.
- **`Bandwidth` is an upper bound, not a switch, in automatic mode.** The encoder picks the audio
  bandwidth from the bitrate as the reference encoder does: at complexity 0 and constant bitrate a
  mono `Voip` stream is wideband at 24 kbit/s, super-wideband at 32 kbit/s and fullband from about
  35 kbit/s. There is no option to force fullband at a lower bitrate.
- **No in-band FEC or DTX on CELT-only configurations.** Requesting either with options that
  select CELT throws `NotSupportedException` at construction instead of being silently ignored.
- **No multistream or surround** (RFC 7845 channel mapping families), **no Ogg container**, no
  RTP packetization. Kiln.Audio reads and writes raw Opus packets.
- **No repacketizer in the public API,** and the encoder writes one packet per call. The decoder
  accepts multi-frame packets.
- **No `float` overload of `DecodeLoss`,** and no API to conceal a specific duration: concealment
  lasts as long as the last decoded packet.
- **No Opus 1.5 extensions** (deep PLC, DRED, LACE/NoLACE).
- **Not bit-identical across CPU families for CELT.** The floating-point CELT kernels differ in
  rounding between kernel sets, so decoded PCM and encoded bytes can differ slightly between, say,
  arm64 and x64. Every kernel set passes the same conformance tests.
- **Not tuned by listening tests.** Quality is gated by objective metrics against the RFC
  reference encoder.

## Documentation

- [docs/architecture.md](docs/architecture.md): modules, encoder mode selection, SIMD kernel sets
- [docs/performance.md](docs/performance.md): measurements against libopus and how to reproduce them
- [docs/robustness.md](docs/robustness.md): the decoder's input contract and the fuzzer
- [docs/test-vectors.md](docs/test-vectors.md): the RFC 8251 conformance vectors
- [docs/releasing.md](docs/releasing.md): how a release is cut
- [CONTRIBUTING.md](CONTRIBUTING.md): build, test and contribution rules
- [SECURITY.md](SECURITY.md): reporting vulnerabilities

## License

Apache-2.0. See [LICENSE](LICENSE). Parts of the codec are derived from the RFC 6716 Appendix A
reference implementation, which is under the Simplified BSD license; the notice is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and ships in the package. libopus is used only as
a test oracle and is not part of this repository or the package.
