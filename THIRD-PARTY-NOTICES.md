# Third-party notices

## RFC 6716 reference implementation (Appendix A)

The following files in `src/` contain code or tables derived from the reference
implementation embedded in Appendix A of RFC 6716:

- `src/Kiln.Audio/Celt/CeltTables.cs`
- `src/Kiln.Audio/Celt/CeltDecoder.cs`
- `src/Kiln.Audio/Celt/CeltPcm.cs`
- `src/Kiln.Audio/Celt/CeltPlc.cs`
- `src/Kiln.Audio/Celt/CeltSynthesizer.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoder.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderAllocation.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderAnalysis.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderBands.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderEnergy.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderKernels.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltEncoderVq.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltPrefilter.cs`
- `src/Kiln.Audio/Celt/Encoder/CeltVbr.cs`
- `src/Kiln.Audio/Celt/Encoder/InputResampler.cs`
- `src/Kiln.Audio/Decoder/CeltOutput.cs`
- `src/Kiln.Audio/Decoder/OpusDecoderCore.cs`
- `src/Kiln.Audio/Decoder/SilkOutput.cs`
- `src/Kiln.Audio/Encoder/OpusHybridEncoder.cs`
- `src/Kiln.Audio/Encoder/OpusModeSelector.cs`
- `src/Kiln.Audio/Encoder/OpusPacketWriter.cs`
- `src/Kiln.Audio/Encoder/OpusRepacketizer.cs`
- `src/Kiln.Audio/Encoder/OpusTocBuilder.cs`
- `src/Kiln.Audio/Encoder/OpusTransitionEncoder.cs`
- `src/Kiln.Audio/OpusDecoder.cs`
- `src/Kiln.Audio/OpusEncoder.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkDtxEncoder.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkLbrrEncoder.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkNsqDelayedDecision.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkNsqDelayedDecisionKernel.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkNsqParameters.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkStereoEncoder.cs`
- `src/Kiln.Audio/Silk/SilkDecoder.cs`
- `src/Kiln.Audio/Silk/SilkFixedPoint.cs`
- `src/Kiln.Audio/Silk/SilkNlsf.cs`
- `src/Kiln.Audio/Silk/SilkPlc.cs`
- `src/Kiln.Audio/Silk/SilkSynthesis.cs`
- `src/Kiln.Audio/Silk/SilkSynthesisTables.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEncoderAnalysis.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEncoderCore.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEncoderMath.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEncoderState.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEncoderTables.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkEntropyEncoder.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkNlsfEncoder.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkNsq.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkPitchAnalysis.cs`
- `src/Kiln.Audio/Silk/Encoder/SilkVad.cs`

`src/Kiln.Audio/Silk/Encoder/SilkInputResampler.cs` is original work, not derived
from the reference implementation.

The reference implementation is distributed under the Simplified BSD license below,
copied verbatim from the `COPYING` file included with the Appendix A source
(RFC 6716, Section A.3; the same notice heads each source file, for example
`celt/modes.c`).

Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic,
                    Jean-Marc Valin, Timothy B. Terriberry,
                    CSIRO, Gregory Maxwell, Mark Borgerding,
                    Erik de Castro Lopo. All rights reserved.

This file is extracted from RFC6716. Please see that RFC for additional
information.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
