# Architecture (planned)

Kiln.Audio is a skeleton. Only the public API stubs and the TOC parser (RFC 6716 §3.1) exist in
`src/`; the test-only conformance oracle is in place. Planned modules, all pure managed C# on
.NET 10:

| Module | Purpose | Status |
|---|---|---|
| Oracle (test-only) | Pinned libopus `opus_demo` / `opus_compare`, the RFC 8251 test vectors, and the managed RFC 6716 §6 quality metric | done |
| Range coder | Entropy coder shared by SILK and CELT (RFC 6716 §4.1, §5.1) | done (decoder + encoder, round-trip tested) |
| Packet framing | TOC parsing, packet splitting, length/padding validation (RFC 6716 §3.2, §3.4) | done |
| SILK | LPC-based speech layer, NB/MB/WB (§4.2) | partial: bitstream (symbol) decode, final-range exact on SILK-only packets (§4.2.3–§4.2.7.8); synthesis not started |
| SILK output resampler | Polyphase minimum-phase FIR, 8/12/16 kHz to 8–48 kHz, streaming and allocation-free | done |
| CELT / MDCT | Transform layer, band energy and PVQ (§4.3) | not started |
| CELT / MDCT kernel | Low-overlap MDCT/IMDCT and the §4.3.7 window, scalar reference | done (scalar; SIMD pending) |
| Hybrid | SILK + CELT combined mode (§3.1, Table 2) | not started |
| PLC / FEC | Packet loss concealment and in-band FEC (LBRR) | not started |
| Resampler-free 48 kHz path | Native 48 kHz operation without a separate resampler stage | not started |
| SIMD kernels | NEON / AVX2 / SSSE3 kernels with scalar parity tests | not started |

## SILK output resampler

`Kiln.Audio.Silk.SilkResampler` converts the SILK internal rate (8, 12 or 16 kHz) to any of
8/12/16/24/48 kHz. It is original work: a rational `L/M` polyphase FIR whose prototype is a
Kaiser-windowed sinc designed in code (no coefficient table is taken from libopus).

* Passband flat within ±0.5 dB to 0.9 × the lower Nyquist frequency; stopband at or above the
  lower Nyquist frequency is at least 60 dB down (the design targets 70 dB).
* The linear-phase prototype is converted to minimum phase with the real cepstrum, which keeps
  the latency small at the cost of a frequency-dependent group delay. `DelaySamplesOut` reports
  the broadband nominal latency (impulse response cross-correlated with an ideal sinc).
* `Process` carries state, allocates nothing, and is bit-identical whether the input arrives in
  one block or in chunks of any size. Equal input and output rates are an exact copy.
* The filter coefficients and all polyphase phases are computed in the constructor; the only
  per-call work is the multiply-accumulate loop.

### Delay relative to the libopus reference

RFC 6716 §4.2.9 leaves the SILK resampler non-normative. The delays below were measured by
cross-correlating libopus 1.5.2's 48 kHz decode of SILK-only streams against a zero-delay
band-limited interpolation of its own internal-rate decode (`SilkResamplerOracleTests`). All
values are output samples at 48 kHz.

| Pair | libopus relative lag | This resampler nominal delay | Measured on speech |
|---|---|---|---|
| 8 → 48 kHz | +3 | 16 | 14 |
| 12 → 48 kHz | −3 | 10 | 8 |
| 16 → 48 kHz | −2 | 7 | 6 |

The libopus lag is small and, for 12 and 16 kHz, negative: its internal-rate decode runs a
"copy" resampler that delays the signal by 4/9/12 input samples relative to the internal SILK
synthesis, while its 48 kHz path delays it by less, so the 48 kHz output appears ahead of the
internal-rate PCM that `opus_demo` exposes. That internal signal is not observable through any
public API, so the copy delay cannot be separated from the conversion delay by cross-correlation,
and a causal, fixed-block converter fed the internal-rate PCM cannot lead its input. Matching the
relative lag to ±0 samples would require either non-causal lookahead or a caller-side shift. The
residual difference is 9–13 output samples (0.19–0.27 ms) and is reported by the oracle tests.

Quality against libopus's 48 kHz decode (12 and 20 kbps, 20 ms frames) is well inside the native
`opus_compare` gate for all three internal rates: 96–97 % for WB and MB, and the native metric
passes for NB as well. Sample-wise SNR is at least 31.9 dB for every measured case.

