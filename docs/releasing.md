# Releasing Proxeno.Kiln.Audio

The package is published to nuget.org from `.github/workflows/release.yml` when a `v*`
tag is pushed. Publishing uses nuget.org **Trusted Publishing** (GitHub OIDC): there is
no API key in the repository or in GitHub secrets.

## One manual prerequisite (owner, once)

Add a Trusted Publishing policy on nuget.org:

1. Sign in to nuget.org as the package owner (`retrotom`).
2. Open **Account settings → Trusted Publishing → Add a new policy**.
3. Set:
   - **Repository Owner**: `Proxeno`
   - **Repository**: `kiln-audio`
   - **Workflow File**: `release.yml`
   - **Environment**: leave empty unless a GitHub Environment is added later.

nuget.org resolves the repository when the policy is first used. A policy created
while the repository cannot be resolved (for example while it is still private) is
only temporarily active, so create or re-check the policy shortly before the first
tag is pushed and confirm it shows as active.

No GitHub secret is needed. The workflow requests a short-lived key with
`NuGet/login@v1` immediately before pushing, and the `user:` input is the public
nuget.org profile name, not a credential.

## Cutting a release

1. Make sure `main` is green and contains everything that should ship, and that
   `PublicAPI.Unshipped.txt` holds only its `#nullable enable` header (move any other
   line into `PublicAPI.Shipped.txt` first).
   The release job runs on a GitHub-hosted runner, so the repository must be public
   (or have hosted-runner minutes) before the tag is pushed.
2. Pick the next SemVer version. MinVer derives the package version from the tag:
   `v1.0.0` → `1.0.0`, `v1.1.0-rc.1` → `1.1.0-rc.1`. An untagged build after
   `v1.0.0` is `1.0.1-preview.0.<height>`; with no tag at all it is
   `0.0.0-preview.0.<height>`.
3. Create and push the tag (a maintainer does this; automation must not):

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

4. Watch the **Release** workflow. It builds, runs the test suite as a gate, packs,
   logs in to nuget.org with OIDC, and pushes the `.nupkg` and `.snupkg`.
5. Verify the package on nuget.org: README, icon, license expression, repository link,
   and the symbol package.
6. Publish a GitHub Release for the tag; the package's release notes link to the
   releases page.

## Local pre-release checks

```bash
dotnet build -c Release                 # 0 warnings
dotnet pack -c Release -o artifacts/pkg # produces Proxeno.Kiln.Audio.*.nupkg + .snupkg
unzip -l artifacts/pkg/Proxeno.Kiln.Audio.*.nupkg
```

The package must contain `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md`,
`icon.png`, `lib/net10.0/Kiln.Audio.dll` and `lib/net10.0/Kiln.Audio.xml`
(the package id is `Proxeno.Kiln.Audio`; the assembly keeps the name
`Kiln.Audio`); the `.snupkg` carries the PDB.

## Native AOT smoke test

`tests/Kiln.Audio.AotSmoke` encodes and decodes one second of audio through the public
API and fails on a bad round trip. It compiles in the normal solution build; publishing
it natively needs a native toolchain (clang, zlib) and is therefore run manually, not on
every PR:

```bash
dotnet publish tests/Kiln.Audio.AotSmoke -c Release -r osx-arm64 -o artifacts/aot-smoke
./artifacts/aot-smoke/Kiln.Audio.AotSmoke
```

Use `-r linux-x64` on Linux. The published binary is self-contained and has no .NET
runtime dependency.

## Public API changes

`src/Kiln.Audio/PublicAPI.Shipped.txt` is the lock on the public surface. Any addition or
removal produces RS0016/RS0017 (both are errors). Deliberate changes:

- add new API lines to `PublicAPI.Unshipped.txt`;
- when a release ships, move the unshipped lines into `PublicAPI.Shipped.txt`.

XML documentation is part of the package (`Kiln.Audio.xml`), and an
undocumented public member fails the build (CS1591).
