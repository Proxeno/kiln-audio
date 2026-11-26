# Test-only helper build for ec_probe on toolchains without a usable `cc` in PATH
# (notably the Windows CI runner). OPUS_SOURCE must point at the extracted libopus tree.
cmake_minimum_required(VERSION 3.16)
project(kiln_ec_probe C)

if(NOT DEFINED OPUS_SOURCE)
  message(FATAL_ERROR "OPUS_SOURCE must be set to the libopus source directory")
endif()

add_executable(ec_probe
  ec_probe.c
  ${OPUS_SOURCE}/celt/entenc.c
  ${OPUS_SOURCE}/celt/entdec.c
  ${OPUS_SOURCE}/celt/entcode.c)

target_include_directories(ec_probe PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}
  ${OPUS_SOURCE}/celt
  ${OPUS_SOURCE}/include)
