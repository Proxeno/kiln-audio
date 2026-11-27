# Architecture

Kiln.Audio is a pure managed C# (.NET 10) Opus codec: a decoder for every Opus mode, and an
encoder with SILK, hybrid and CELT paths (automatic per-frame selection in the mono 48 kHz case).
Everything in `src/` is managed code. The test-only conformance oracle (libopus built from source)
lives under `.oracle` and `tests/Kiln.Audio.Tests/Oracle`; it is not part of the shipped package.
The status of each module is below.

| Module | Purpose | Status |
|---|---|---|
| Oracle (test-only) | Pinned libopus `opus_demo` / `opus_compare`, the RFC 8251 test vectors, and the managed RFC 6716 §6 quality metric | done |
| Range coder | Entropy coder shared by SILK and CELT (RFC 6716 §4.1, §5.1), `Entropy/RangeDecoder.cs` and `RangeEncoder.cs` | done (decoder + encoder, round-trip tested) |
| Packet framing | TOC parsing, packet splitting, length/padding validation (RFC 6716 §3.2, §3.4) | done |
| SILK decoder | LPC-based speech layer, NB/MB/WB (§4.2) | done: bitstream decode and bit-exact synthesis at the internal rate, mono and stereo (§4.2.3–§4.2.9), plus packet loss concealment, comfort noise and in-band FEC (§4.2.4–§4.2.5, §4.4) |
| SILK output resampler | Polyphase minimum-phase FIR, 8/12/16 kHz to 8–48 kHz, streaming and allocation-free | done |
| CELT decoder | Band energy, PVQ and synthesis (§4.3) | done: bitstream decode through synthesis, mono/stereo, every duration and bandwidth, including the band limit before API-rate decimation (§4.3.1–§4.3.7) |
| CELT / MDCT | Real-input MDCT/IMDCT (one N/4-point complex FFT per direction) and the §4.3.7 window, `Celt/Mdct.cs`, `Celt/Fft.cs` | done; the CELT decode and synthesis path is generic over the SIMD kernel tier (see SIMD kernel sets) |
| CELT encoder | CELT-only encode: mono/stereo, 8–48 kHz API rates, 2.5–20 ms, CBR/VBR, TF/spreading/allocation/PVQ and the §4.3.7.1 pitch pre-filter, `Celt/Encoder/` | done: G1 interop and G3 allocation/determinism tests over every mode; quality measured against libopus (see performance.md) |
| SILK encoder | Mono and stereo SILK encode: VAD, pitch and LPC analysis, NLSF quantization, delayed-decision noise-shaping quantizer, rate control, `Silk/Encoder/` | done: 10–60 ms frames, NB/MB/WB, any API rate; segmental SNR gated against the frozen RFC 6716 Appendix A encoder (see performance.md) |
| Hybrid encoder, mode selection, transitions | `Encoder/OpusHybridEncoder.cs` (SILK wideband at 16 kHz plus CELT from band 17 in one range coder), `OpusModeSelector.cs` (per-frame SILK/hybrid/CELT choice with hysteresis, from the reference's thresholds), `OpusTransitionEncoder.cs` (§4.5.1 5 ms redundant CELT frames) | done for the automatic path (mono, 48 kHz, 10/20 ms, see Encoder mode selection) |
| In-band FEC (LBRR) encode | Low-bitrate redundancy frames in SILK-only and hybrid packets (§4.2.4–§4.2.5), `Silk/Encoder/SilkLbrrEncoder.cs`, `SilkStereoPacketEncoder.cs` | done for SILK-only and hybrid frames; not supported on the CELT-only path |
| DTX | Discontinuous transmission, a one-byte TOC-only packet for suppressed frames, `Silk/Encoder/SilkDtxEncoder.cs` | done on the SILK-only path; rejected with `NotSupportedException` on the CELT-only path; turns automatic mode selection off |
| Packet writer / repacketizer | `Encoder/OpusPacketWriter.cs` (TOC plus frames, code 0–3 layout, padding), `OpusRepacketizer.cs` (join/split at frame level, as the reference `repacketizer.c`), `OpusTocBuilder.cs` | done; internal types, not part of the public API |
| Top-level decoder | Public `OpusDecoder`: packet decode, mode dispatch, transition redundancy, resets, `FinalRange` | done: all 12 RFC 8251 vectors pass `opus_compare` and the final range through the public API |
| Top-level encoder | Public `OpusEncoder`: option validation, path selection, `Encode` for 16-bit and float PCM, `FinalRange` | done |
| PLC / FEC decode | Packet loss concealment and in-band FEC (LBRR) | done: SILK pitch/LPC extrapolation with CNG and re-entry glue, CELT pitch/noise concealment, mode-aware hybrid PLC, and in-band LBRR FEC decode (§4.2.4–§4.2.5, §4.4) |
| SIMD kernel sets | Scalar, portable `Vector128`, arm64 `AdvSimd` and x64 AVX2+FMA kernel sets for CELT and SILK decode and encode, with scalar parity tests | done (see SIMD kernel sets) |
| Resampler-free 48 kHz path | Native 48 kHz operation without a separate resampler stage | not planned for 1.0 |

## Source layout

One line per folder under `src/Kiln.Audio`; the public types (`OpusDecoder`, `OpusEncoder`, their
options, `OpusApplication`, `OpusBandwidth`, `OpusFrameDuration`, `OpusFecOutcome`) sit in the
folder root.

- `Celt/`: CELT decoder, synthesizer, MDCT/FFT, window, PLC; `Celt/Encoder/` is the CELT encoder.
- `Decoder/`: `OpusDecoderCore` (packet and frame decoder behind `OpusDecoder`), `SilkOutput` and
  `CeltOutput` (decode layers to API-rate PCM).
- `Encoder/`: mode selector, hybrid encoder, transition encoder, packet writer, repacketizer, TOC
  builder.
- `Entropy/`: range decoder and range encoder.
- `Silk/`: SILK decoder, synthesis, tables, PLC, output resampler; `Silk/Encoder/` is the SILK
  encoder (analysis, NSQ, entropy coding, LBRR, DTX, stereo).
- `Simd/`: scalar/portable kernels and, under `Simd/Kernels/`, the per-ISA kernel sets.

## Encoder mode selection

`OpusEncoder` picks one of three paths in its constructor from `OpusEncoderOptions`:

- **Automatic per-frame SILK, hybrid or CELT selection** applies when `Application` is `Voip` or
  `Audio`, the input is mono at 48 kHz, frames are 10 or 20 ms, and DTX is off. Each frame is coded
  as SILK-only, hybrid or CELT-only by `OpusModeSelector`. In-band FEC is supported on this path.
- **SILK-only** applies, when the automatic case does not, for `Voip` with a narrowband,
  mediumband, wideband or unset bandwidth at 10, 20, 40 or 60 ms, at any sample rate, mono or
  stereo. In-band FEC and DTX are supported.
- **CELT-only** is everything else (40 and 60 ms are packed as two or three 20 ms frames).
  Requesting in-band FEC or DTX on this path makes the constructor throw `NotSupportedException`.

## SIMD kernel sets

Kernels are grouped into sets, one per instruction-set tier, in `Simd/Kernels/`. A tier is a
`struct` implementing the kernel interfaces (`ICeltKernels`, `ISilkKernels`, `ISilkEncoderKernels`,
`IEncoderKernels`) with static abstract members, and the codec classes are generic over the tier,
so the JIT compiles one body per tier and a kernel call inside a frame is a direct, inlinable call
(no per-call dispatch; NativeAOT-safe).

| Tier | Selected when |
|---|---|
| `Avx2FmaKernels` | x64 with AVX2 and FMA3 (128-bit VEX forms with FMA; no `Vector256`) |
| `AdvSimdArm64Kernels` | arm64 (`AdvSimd` and `AdvSimd.Arm64` directly, with fused multiply-add) |
| `Vector128Kernels` | any other host where `Vector128` is accelerated (for example SSE2-only x64, WASM) |
| `ScalarKernels` | otherwise, and whenever `KILN_AUDIO_DISABLE_SIMD` is `1` or `true` |

`AudioKernels.Select()` is called once in each decoder or encoder constructor and tests the tiers
in the order above. The environment variable is read once per process (`SimdConfig`). Each vector
tier is checked against the scalar tier by parity tests (`KernelTierParityTests`,
`SilkDecodeKernelTierParityTests`, `SilkKernelTierParityTests`, `SilkNsqLaneParityTests`,
`SimdParityTests`); integer SILK kernels are bit-identical to scalar, float CELT kernels are within
a bound relative to the signal RMS. The x64 tier cannot run on the arm64 development machine and is
covered by the same tests on x64 CI. See performance.md for the measured effect (arm64 only).

## SILK synthesis

`Kiln.Audio.Silk.SilkSynthesis` reconstructs PCM from the parameters `SilkDecoder` produces
(RFC 6716 §4.2.7.5.2–§4.2.9). It is a direct port of the fixed-point reference decoder, so its
output is bit-exact with libopus: NLSF reconstruction, stabilization (with the RFC 8251 §7
saturating addition), interpolation, NLSF-to-LPC conversion, LPC range and prediction-gain
limiting, LTP synthesis with rewhitening, LPC synthesis, and stereo mid/side unmixing. It carries
the LPC/LTP histories, previous NLSFs and gains, and the stereo mid/side state across frames, and
resets them on a bandwidth or channel change (RFC 6716 §4.5.2, RFC 8251 §3). Synthesizing a
frame allocates nothing.

The output is the signal libopus feeds to its internal-rate resampler. libopus always runs that
resampler, even when the requested rate equals the internal rate; its copy path delays the signal
by a fixed number of samples. `SilkPcmOracleTests` measures these delays by exact search and
asserts every sample after alignment:

| Internal rate | Mono delay (samples/channel) | Stereo delay (samples/channel) |
|---|---|---|
| 8 kHz | 5 | 4 |
| 12 kHz | 10 | 9 |
| 16 kHz | 13 | 12 |

Mono is delayed by one sample more than stereo because the mono path buffers the two-sample mid
history before the resampler (`dec_API.c`). In an interleaved stereo stream the measured delay is
twice the per-channel value (8, 18 and 24 samples).

## SILK output resampler

`Kiln.Audio.Silk.SilkResampler` converts the SILK internal rate (8, 12 or 16 kHz) to any of
8/12/16/24/48 kHz. It is original work: a rational `L/M` polyphase FIR whose prototype is a
Kaiser-windowed sinc designed in code (no coefficient table is taken from libopus).

* Passband flat within ±0.5 dB to 0.9 × the lower Nyquist frequency; stopband at or above the
  lower Nyquist frequency is at least 60 dB down (the design targets 70 dB).
* The linear-phase prototype is converted to minimum phase with the real cepstrum, which keeps
  the latency small at the cost of a frequency-dependent group delay.
* An integer alignment delay follows the filter so the total delay matches libopus's decode-to-
  output delay for every rate pair and channel count. `DelaySamplesOut` reports the filter's
  nominal delay plus that alignment.
* `Process` carries state, allocates nothing, and is bit-identical whether the input arrives in
  one block or in chunks of any size. Equal input and output rates are a pure delay, not a copy.
* The filter coefficients and all polyphase phases are computed in the constructor; the only
  per-call work is the multiply-accumulate loop.

### Delay relative to the libopus reference

RFC 6716 §4.2.9 leaves the SILK resampler non-normative, but end-to-end alignment with the
reference decoder is useful for conformance testing, so the resampler is calibrated to libopus
1.5.2. libopus's internal-rate decode runs a "copy" resampler that delays the SILK synthesis by
5/10/13 input samples for mono and 4/9/12 for stereo at 8/12/16 kHz; its 48 kHz path delays the
same synthesis differently. The integer alignment delay compensates for both that copy-path
offset and the difference between libopus's resampler and this filter, so that SILK synthesis
followed by this resampler lands at lag 0 against `opus_demo -d <rate>`.

| Input | Output | Mono alignment | Stereo alignment |
|---|---|---|---|
| 8 kHz | 8/12/16/24/48 kHz | 5/5/6/10/19 | 4/4/4/7/13 |
| 12 kHz | 8/12/16/24/48 kHz | 5/10/10/16/29 | 4/9/8/14/25 |
| 16 kHz | 8/12/16/24/48 kHz | 5/8/13/16/31 | 4/7/12/14/28 |

The values are a calibration of this filter and must be re-measured if the prototype design
changes; they are not libopus coefficients. `ResampleConformanceTests` checks the 24 SILK-DEC-2
streams at 48 kHz: the cross-correlation peak is at lag 0 for every one, and the sample-wise SNR
after the first 10 ms is 29.9–33.8 dB, above a 29.5 dB regression floor. That SNR measures
resemblance to libopus's particular resampler, not quality: an ideal interpolator is only about
20 dB from libopus on NB, and this filter is deliberately an original anti-aliased design rather
than a copy of the reference's.

### Mono/stereo channel transitions

RFC 8251 vectors 02–04 are mono for their first half and stereo for the second. A change of
channel count is not a mode change: RFC 6716 §4.5.2 resets only for a rate or mode change, and
RFC 8251 §3 resets only the stereo state. The reference implementation keeps the mid (channel 0)
decoder and synthesis state across the switch, initializes the new side channel, and clears the
stereo mid/side prediction memory (`silk/dec_API.c` in Appendix A). `SilkDecoder`,
`SilkSynthesis` and `SilkVectorDriver` follow that: the driver also skips one internal-rate
sample at a mono-to-stereo switch so the one-sample delay step is applied before the resampler
and smoothed by it, rather than as a discontinuity in the 48 kHz output.

With that handling, RFC 8251 vectors 02, 03 and 04 pass the native `opus_compare` metric against
both `.dec` and `m.dec` (see `ResampleConformanceTests`), and `ResampleTransitionTests` checks
that the mono half and the post-transition stereo half are bit-exact with `opus_demo` at the
internal rate.

