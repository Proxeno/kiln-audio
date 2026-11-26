# Architecture

Kiln.Audio is a pure managed C# (.NET 10) Opus decoder; the encoder is planned. The status of
each module is below. Everything in `src/` is managed code, and the test-only conformance oracle
is in place.

| Module | Purpose | Status |
|---|---|---|
| Oracle (test-only) | Pinned libopus `opus_demo` / `opus_compare`, the RFC 8251 test vectors, and the managed RFC 6716 §6 quality metric | done |
| Range coder | Entropy coder shared by SILK and CELT (RFC 6716 §4.1, §5.1) | done (decoder + encoder, round-trip tested) |
| Packet framing | TOC parsing, packet splitting, length/padding validation (RFC 6716 §3.2, §3.4) | done |
| SILK | LPC-based speech layer, NB/MB/WB (§4.2) | done: bitstream decode and bit-exact synthesis at the internal rate, mono and stereo (§4.2.3–§4.2.9); no PLC/FEC |
| SILK output resampler | Polyphase minimum-phase FIR, 8/12/16 kHz to 8–48 kHz, streaming and allocation-free | done |
| CELT / MDCT | Transform layer, band energy and PVQ (§4.3) | done: bitstream decode through synthesis, mono/stereo, every duration and bandwidth, including the band limit before API-rate decimation (§4.3.1–§4.3.7) |
| CELT / MDCT kernel | Low-overlap MDCT/IMDCT and the §4.3.7 window, scalar reference | done (scalar; SIMD pending) |
| Hybrid | SILK + CELT combined mode (§3.1, Table 2) | done: SILK WB at 16 kHz plus CELT from band 17, with the §4.5 transition handling |
| Top-level decoder | Public `OpusDecoder`: packet decode, mode dispatch, transition redundancy, resets, `FinalRange` | done: all 12 RFC 8251 vectors pass `opus_compare` and the final range through the public API |
| PLC / FEC | Packet loss concealment and in-band FEC (LBRR) | partial: minimal concealment (CELT `DecodeLost`, SILK/hybrid decaying repeat); no LBRR FEC |
| Resampler-free 48 kHz path | Native 48 kHz operation without a separate resampler stage | not started |
| SIMD kernels | NEON / AVX2 / SSSE3 kernels with scalar parity tests | not started |

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

