#!/usr/bin/env bash
# Fetches the official RFC 8251 Opus test vectors into
# tests/Kiln.Audio.Tests/Fixtures/opus_testvectors (never committed).
#
# The vectors are the conformance material for RFC 6716 as updated by RFC 8251:
# decoded output from any conformant decoder must match the .dec reference
# within the opus_compare quality metric.
#
# Idempotent: if the stamp file matches the pinned digest, nothing is done.
set -euo pipefail

URL="https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz"
SHA256_EXPECTED="6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
fixtures_dir="${repo_root}/tests/Kiln.Audio.Tests/Fixtures/opus_testvectors"
stamp_file="${fixtures_dir}/.sha256"

sha256_of() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

if [[ -f "${stamp_file}" ]]; then
    current="$(tr -d '[:space:]' < "${stamp_file}")"
    if [[ "${current}" == "${SHA256_EXPECTED}" ]]; then
        echo "RFC 8251 test vectors already present (sha256 ${SHA256_EXPECTED})."
        exit 0
    fi
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/kiln-opus-vectors.XXXXXX")"
trap 'rm -rf "${tmp_dir}"' EXIT

echo "Downloading ${URL}"
curl -fL --retry 3 --retry-delay 2 -o "${tmp_dir}/vectors.tar.gz" "${URL}"

actual="$(sha256_of "${tmp_dir}/vectors.tar.gz")"
if [[ "${actual}" != "${SHA256_EXPECTED}" ]]; then
    echo "SHA-256 mismatch for ${URL}" >&2
    echo "  expected: ${SHA256_EXPECTED}" >&2
    echo "  actual:   ${actual}" >&2
    exit 1
fi

mkdir -p "${tmp_dir}/extracted"
tar -xzf "${tmp_dir}/vectors.tar.gz" -C "${tmp_dir}/extracted"

# The archive either puts the vectors at its root or inside a single directory.
source_dir="${tmp_dir}/extracted"
if [[ ! -f "${source_dir}/testvector01.bit" ]]; then
    source_dir="$(find "${tmp_dir}/extracted" -type f -name testvector01.bit -exec dirname {} \; | head -n 1)"
fi
if [[ -z "${source_dir}" || ! -f "${source_dir}/testvector01.bit" ]]; then
    echo "testvector01.bit not found in ${URL}" >&2
    exit 1
fi

# Keep tracked files (SOURCE.md, the committed .bit packets) and replace only the
# vector outputs the archive provides.
mkdir -p "${fixtures_dir}"
find "${fixtures_dir}" -maxdepth 1 -type f -name 'testvector*' -delete
cp -R "${source_dir}/." "${fixtures_dir}/"
printf '%s\n' "${SHA256_EXPECTED}" > "${stamp_file}"

echo "Extracted RFC 8251 test vectors into ${fixtures_dir}:"
find "${fixtures_dir}" -maxdepth 1 -type f -exec basename {} \; | sort
