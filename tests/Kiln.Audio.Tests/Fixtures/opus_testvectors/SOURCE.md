# Source of these test vectors

- URL: <https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz>
- Tarball SHA-256: `6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55`
- Retrieved: 2026-11-25
- RFC 8251 test vectors, redistribution approved by owner 2026-11-26.

Only the `testvectorNN.bit` packet files are committed here (3.4 MB total). The extracted set is
121 MB (3.4 MB of `.bit` plus 117 MB of `.dec`), above the 60 MB commit threshold, so the `.dec`
reference decodes are generated instead by `scripts/fetch-test-vectors.sh`. That script verifies
the tarball against the SHA-256 above and rewrites every `testvector*` file; this file is
preserved.
