# Kiln.Audio

[![CI](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml/badge.svg)](https://github.com/Proxeno/kiln-audio/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-e8912d)](https://github.com/Proxeno/kiln-audio/blob/main/LICENSE)

**A from-scratch Opus encoder and decoder for .NET.** Pure managed, SIMD-accelerated C#, zero native
dependencies, Apache-2.0. Kiln.Audio is the audio sibling of [Kiln](https://github.com/Proxeno/kiln),
the H.264 encoder: the goal is a standards-compliant Opus (RFC 6716) codec for real-time voice,
implemented entirely in this repository, with no native codec to cross-compile, ship, or keep patched.

> **Status: skeleton — no codec yet.**

## Goals

- **Pure managed.** 100% C# on .NET 10, no native library.
- **SIMD.** NEON/AdvSimd on arm64, AVX2 and SSSE3 on x64, scalar fallback everywhere else.
- **Apache-2.0, no copyleft code, no native code.** Parts are derived from the RFC 6716 reference (Simplified BSD; see THIRD-PARTY-NOTICES.md).
- **Real-time voice** for Keryx / Haiyu: low, predictable per-frame latency.
- **Verified against libopus as an oracle** (`opus_demo`, IETF / RFC 8251 test vectors).

## What Kiln.Audio is not (yet)

- Not a working codec: `OpusEncoder.Encode` and `OpusDecoder.Decode` throw `NotImplementedException`.
- Not a SILK, CELT or hybrid implementation; none of the range coder, MDCT or LPC machinery exists.
- Not packet-loss concealment or in-band FEC.
- Not tested against any oracle vectors yet.
- What does exist: the public API shape, options validation, and an RFC 6716 §3.1 TOC-byte parser.

## Documentation

- [Architecture (planned modules)](docs/architecture.md)
- [Contributing](CONTRIBUTING.md)

## License

Apache-2.0. See [LICENSE](LICENSE).
