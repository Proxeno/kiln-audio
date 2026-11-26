# Kiln.Audio

[![CI](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml/badge.svg)](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-e8912d)](https://github.com/Proxeno/kiln-audio/blob/main/LICENSE)

**A from-scratch Opus encoder and decoder for .NET.** Pure managed, SIMD-accelerated C#, zero native
dependencies, Apache-2.0. Kiln.Audio is the audio sibling of [Kiln](https://github.com/Proxeno/kiln),
the H.264 encoder: the goal is a standards-compliant Opus (RFC 6716) codec for real-time voice,
implemented entirely in this repository, with no native codec to cross-compile, ship, or keep patched.

> **Status: RFC 8251-conformant decoder; encoder in progress.**

## Goals

- **Pure managed.** 100% C# on .NET 10, no native library.
- **SIMD.** NEON/AdvSimd on arm64, AVX2 and SSSE3 on x64, scalar fallback everywhere else.
- **Apache-2.0, no copyleft code, no native code.** Parts are derived from the RFC 6716 reference (Simplified BSD; see THIRD-PARTY-NOTICES.md).
- **Real-time voice** for Keryx / Haiyu: low, predictable per-frame latency.
- **Verified against libopus as an oracle** (`opus_demo`, IETF / RFC 8251 test vectors).

## What Kiln.Audio is not (yet)

- Not an encoder: `OpusEncoder.Encode` still throws `NotImplementedException`.
- Not full packet-loss concealment or in-band FEC: `DecodeLoss` is minimal (CELT `DecodeLost`,
  SILK/hybrid decaying repeat) and `decodeFec: true` throws `NotSupportedException`.

## What exists

- The public `OpusDecoder`: SILK-only, hybrid and CELT-only packets at 8/12/16/24/48 kHz and
  one or two channels, RFC 8251 transition redundancy, the §4.5.2 reset rules, and `FinalRange`.
- All 12 RFC 8251 vectors pass `opus_compare` through the public API at 48 kHz stereo, the final
  range matches for every packet, and `Decode` allocates nothing after construction.
- The range coder, MDCT/IMDCT, LPC/LTP synthesis, CELT PVQ, the SILK output resampler and an
  RFC 6716 §3.1–§3.4 packet parser.
- `DecodeLoss` and the SILK resampler are documented as minimal/non-normative (see
  `docs/architecture.md` and RFC 6716 §4.2.9).

## Documentation

- [Architecture (planned modules)](docs/architecture.md)
- [Contributing](CONTRIBUTING.md)

## License

Apache-2.0. See [LICENSE](LICENSE).
