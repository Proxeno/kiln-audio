# Architecture (planned)

Kiln.Audio is a skeleton. Only the public API stubs and the TOC parser (RFC 6716 §3.1) exist in
`src/`; the test-only conformance oracle is in place. Planned modules, all pure managed C# on
.NET 10:

| Module | Purpose | Status |
|---|---|---|
| Oracle (test-only) | Pinned libopus `opus_demo` / `opus_compare`, the RFC 8251 test vectors, and the managed RFC 6716 §6 quality metric | done |
| Range coder | Entropy coder shared by SILK and CELT (RFC 6716 §4.1) | not started |
| SILK | LPC-based speech layer, NB/MB/WB (§4.2) | not started |
| CELT / MDCT | Transform layer, band energy and PVQ (§4.3) | not started |
| Hybrid | SILK + CELT combined mode (§3.1, Table 2) | not started |
| PLC / FEC | Packet loss concealment and in-band FEC (LBRR) | not started |
| Resampler-free 48 kHz path | Native 48 kHz operation without a separate resampler stage | not started |
| SIMD kernels | NEON / AVX2 / SSSE3 kernels with scalar parity tests | not started |
