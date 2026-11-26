using System.IO;
using FluentAssertions;
using Kiln.Audio.Fuzz;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// Replays the decoder sequences the fuzzer found and minimized. Each file is the exact input
/// that violated the resilience contract before its fix, committed under
/// <c>Robustness/Inputs</c> and replayed on a fresh decoder.
/// </summary>
public class FuzzSequenceRegressionTests
{
    [Theory]
    [InlineData("silk-plc-short-after-long-24k-mono.seq")]
    [InlineData("silk-plc-short-after-long-8k-stereo.seq")]
    [InlineData("celt-plc-noise-loss-then-pitch.seq")]
    public void A_found_sequence_decodes_without_an_undocumented_failure(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Robustness", "Inputs", name);
        IReadOnlyList<FuzzStep> steps = FuzzSequenceFile.Read(path);

        steps.Should().NotBeEmpty();
        Action replay = () => DecoderTarget.TraceSequence(steps);
        replay.Should().NotThrow("the committed input is the minimized crash; the fix must keep it inside the documented contract");
    }
}
