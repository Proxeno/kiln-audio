#!/usr/bin/env bash
# Builds the pinned libopus reference tools into .oracle/bin (never committed).
#
# These binaries are a TEST-ONLY oracle: opus_demo decodes the RFC 8251 test
# vectors and opus_compare scores PCM against the reference decodes. Nothing
# under src/ may reference them. ec_probe exposes libopus's range coder for the
# bit-exact differential tests in tests/Kiln.Audio.Tests/Entropy.
#
# Output: .oracle/bin/opus_demo(.exe), .oracle/bin/opus_compare(.exe),
#         .oracle/bin/ec_probe(.exe)
set -euo pipefail

VERSION="1.5.2"
URL="https://downloads.xiph.org/releases/opus/opus-${VERSION}.tar.gz"
SHA256_EXPECTED="65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
oracle_root="${repo_root}/.oracle"
src_dir="${oracle_root}/src"
build_dir="${oracle_root}/build"
bin_dir="${oracle_root}/bin"
stamp_file="${bin_dir}/.sha256"

exe_suffix=""
case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) exe_suffix=".exe" ;;
esac

is_windows=0
if [[ -n "${exe_suffix}" ]]; then
    is_windows=1
fi

sha256_of() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

if [[ -f "${stamp_file}" ]]; then
    current="$(tr -d '[:space:]' < "${stamp_file}")"
    if [[ "${current}" == "${SHA256_EXPECTED}" \
        && -f "${bin_dir}/opus_demo${exe_suffix}" \
        && -f "${bin_dir}/opus_compare${exe_suffix}" \
        && -f "${bin_dir}/ec_probe${exe_suffix}" ]]; then
        echo "libopus ${VERSION} oracle already built (sha256 ${SHA256_EXPECTED})."
        exit 0
    fi
fi

find_tool() {
    local name="$1"
    find "${build_dir}" -type f \( -name "${name}" -o -name "${name}.exe" \) -print -quit
}

# Builds the test-only range-coder differential probe. It links only the three
# libopus entropy sources plus our own ec_probe.c; nothing here reaches src/.
build_probe() {
    local source="$1"
    local native_dir="${repo_root}/tests/Kiln.Audio.Tests/Oracle/native"
    local probe_src="${native_dir}/ec_probe.c"
    local probe_out="${bin_dir}/ec_probe${exe_suffix}"
    local entry_src="${source}/celt/entenc.c"
    local dec_src="${source}/celt/entdec.c"
    local common_src="${source}/celt/entcode.c"

    if [[ "${is_windows}" -eq 1 ]]; then
        # No dependable cc in the Windows runner's bash; use the CMake toolchain.
        local probe_build="${build_dir}/probe"
        if ! cmake -S "${native_dir}" -B "${probe_build}" \
            -DOPUS_SOURCE="${source}" -DCMAKE_BUILD_TYPE=Release; then
            return 1
        fi
        if ! cmake --build "${probe_build}" --config Release; then
            return 1
        fi
        local built
        built="$(find "${probe_build}" -type f \( -name "ec_probe" -o -name "ec_probe.exe" \) -print -quit)"
        if [[ -z "${built}" ]]; then
            return 1
        fi
        cp "${built}" "${probe_out}"
        return 0
    fi

    "${CC:-cc}" -O2 \
        -I"${source}/celt" -I"${source}/include" \
        -o "${probe_out}" \
        "${probe_src}" "${entry_src}" "${dec_src}" "${common_src}"
}

build_oracle() {
    tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/kiln-libopus.XXXXXX")"
    trap 'rm -rf "${tmp_dir}"' EXIT

    echo "Downloading ${URL}"
    curl -fL --retry 3 --retry-delay 2 -o "${tmp_dir}/opus.tar.gz" "${URL}"

    local actual
    actual="$(sha256_of "${tmp_dir}/opus.tar.gz")"
    if [[ "${actual}" != "${SHA256_EXPECTED}" ]]; then
        echo "SHA-256 mismatch for ${URL}" >&2
        echo "  expected: ${SHA256_EXPECTED}" >&2
        echo "  actual:   ${actual}" >&2
        exit 1
    fi

    rm -rf "${src_dir}" "${build_dir}" "${bin_dir}"
    mkdir -p "${src_dir}" "${bin_dir}"
    tar -xzf "${tmp_dir}/opus.tar.gz" -C "${src_dir}"
    local source="${src_dir}/opus-${VERSION}"
    if [[ ! -f "${source}/src/opus_compare.c" ]]; then
        echo "Unexpected archive layout: ${source}/src/opus_compare.c missing" >&2
        exit 1
    fi

    cmake -S "${source}" -B "${build_dir}" \
        -DCMAKE_BUILD_TYPE=Release \
        -DOPUS_BUILD_PROGRAMS=ON \
        -DOPUS_BUILD_TESTING=OFF \
        -DOPUS_BUILD_SHARED_LIBRARY=OFF
    cmake --build "${build_dir}" --config Release --parallel 2

    local demo
    demo="$(find_tool opus_demo)"
    if [[ -z "${demo}" ]]; then
        echo "opus_demo was not produced by the build." >&2
        exit 1
    fi
    cp "${demo}" "${bin_dir}/opus_demo${exe_suffix}"

    local compare
    compare="$(find_tool opus_compare)"
    if [[ -n "${compare}" ]]; then
        cp "${compare}" "${bin_dir}/opus_compare${exe_suffix}"
    elif [[ "${is_windows}" -eq 0 ]]; then
        # Some libopus releases do not add opus_compare to the CMake build;
        # it is a standalone C file with no libopus dependency.
        local cc_bin="${CC:-cc}"
        "${cc_bin}" -O2 -o "${bin_dir}/opus_compare" "${source}/src/opus_compare.c" -lm
    else
        echo "opus_compare was not produced by the CMake build on Windows." >&2
        return 1
    fi

    chmod +x "${bin_dir}/opus_demo${exe_suffix}" "${bin_dir}/opus_compare${exe_suffix}"

    if build_probe "${source}"; then
        chmod +x "${bin_dir}/ec_probe${exe_suffix}"
    elif [[ "${is_windows}" -eq 1 ]]; then
        echo "ec_probe unavailable on this OS (Windows); range-coder oracle tests will skip." >&2
    else
        echo "ec_probe (range-coder probe) failed to build." >&2
        return 1
    fi

    printf '%s\n' "${SHA256_EXPECTED}" > "${stamp_file}"
    echo "Built libopus ${VERSION} oracle into ${bin_dir}:"
    ls -l "${bin_dir}"
}

if ! build_oracle; then
    if [[ "${is_windows}" -eq 1 ]]; then
        echo "oracle unavailable on this OS (Windows): opus_compare was not built." >&2
        exit 0
    fi
    exit 1
fi
