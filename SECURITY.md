# Security policy

## Supported versions

Security fixes land on `main` and in the latest published minor version
only; there are no long-term support branches.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub Security Advisories:

- Go to <https://github.com/Proxeno/kiln-audio/security/advisories/new>
  (Security tab → Report a vulnerability), or
- Use the **Report a vulnerability** button on the repository's Security tab.

That creates a private thread visible only to the maintainers and to you.

Please include, as far as you can:

- what the issue is, and what an attacker or a malformed packet gets you;
- the Kiln.Audio version (or commit) and target platform/architecture (x64
  AVX2, x64 SSSE3, arm64 NEON, or scalar fallback) — SIMD-specific bugs often
  don't reproduce across kernel sets;
- a minimal reproduction: the options, and the packet or PCM data if the issue
  depends on content;
- whether disabling hardware intrinsics changes the behavior, if you've
  checked.

We will acknowledge the report, keep you updated while we investigate, and
credit you in the advisory when it is published, unless you would rather
stay anonymous.

## Scope

Kiln.Audio is a library, not a service. A decoder is, by nature, exposed to
untrusted network input, so memory-safety issues are always in scope:

- Kiln.Audio has **no native dependencies**; a bounds-check gap is a bug in
  Kiln.Audio, not a wrapped native library.
- Hot paths use `unsafe` code and hardware intrinsics.
  Out-of-bounds reads/writes, uninitialized-memory disclosure, or
  SIMD-vs-scalar divergence are the class of bug this policy most wants
  reported.
- Crashes, panics, or hangs on malformed or adversarial packets (including
  malformed TOC bytes and frame-count codes) are in scope.

The decoder's input contract (which exceptions a malformed packet may raise,
and what the fuzz suite asserts) is in [docs/robustness.md](docs/robustness.md).
A violation of that contract is a security bug.

Out of scope:

- Denial of service from simply feeding the codec more data than your system
  can handle — a capacity-planning problem for the embedding application.
- Issues that require an attacker to already control the process embedding
  Kiln.Audio.
- Findings from automated scanners with no demonstrated impact specific to
  this codebase.

If you're unsure whether something is in scope, report it privately anyway.
