using System;
using System.Collections.Concurrent;
using FluentAssertions;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Proves the RFC 8251 test vectors against the managed Opus quality metric and against a
/// pinned libopus build used purely as a black-box oracle.
/// </summary>
/// <remarks>
/// Every test returns without asserting when the fixtures or the oracle are missing; set
/// <c>KILN_AUDIO_REQUIRE_ORACLE=1</c> to make that a failure instead (CI does this where the
/// oracle builds). Expensive analyses are cached per vector for the lifetime of the test run.
/// </remarks>
public class OracleTests
{
    private const int Rate = 48000;

    /// <summary>
    /// Vectors whose two conformant reference decodes do not compare as passing against each other.
    /// RFC 8251 only requires a decoder's output to match either reference; the metric is not a
    /// distance. These values are measured, and the native tool reports the same verdict.
    /// </summary>
    private static readonly HashSet<int> ReferenceDecodesThatDiffer = [7, 9];

    private static readonly ConcurrentDictionary<(int Vector, Scenario Scenario), Lazy<Input>> Inputs = new();
    private static readonly ConcurrentDictionary<(int Vector, Scenario Scenario), Lazy<OpusCompare.Result>> Metrics = new();

    private readonly ITestOutputHelper _output;

    public OracleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private enum Scenario
    {
        Self,
        Modified,
        Corrupted,
        Decoded,
    }

    public static TheoryData<int> Vectors()
    {
        var data = new TheoryData<int>();
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            data.Add(vector);
        }

        return data;
    }

    /// <summary>
    /// (a) Each reference decode passes against itself, and the .dec/m.dec cross-comparison has the
    /// verdict the native tool reports.
    /// </summary>
    /// <param name="vector">Vector number.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Rfc8251_reference_decodes_have_expected_verdicts(int vector)
    {
        if (!OracleTestSupport.VectorsAvailable())
        {
            return;
        }

        OpusCompare.Result self = GetMetric(vector, Scenario.Self);
        OpusCompare.Result modified = GetMetric(vector, Scenario.Modified);
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2}: self {self.Quality:F4} %, m.dec {modified.Quality:F4} %"));

        self.Passed.Should().BeTrue(Describe(vector, "reference against itself", self));
        modified.Passed.Should().Be(
            !ReferenceDecodesThatDiffer.Contains(vector),
            Describe(vector, "reference against m.dec", modified));
    }

    /// <summary>(b) A deterministically corrupted decode fails the metric.</summary>
    /// <param name="vector">Vector number.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Corrupted_output_fails_the_metric(int vector)
    {
        if (!OracleTestSupport.VectorsAvailable())
        {
            return;
        }

        OpusCompare.Result corrupted = GetMetric(vector, Scenario.Corrupted);
        corrupted.Passed.Should().BeFalse(Describe(vector, "reference against corrupted output", corrupted));
    }

    /// <summary>(c) The managed metric and the native <c>opus_compare</c> agree.</summary>
    /// <param name="vector">Vector number.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Managed_metric_agrees_with_opus_compare(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        Scenario[] scenarios = [Scenario.Self, Scenario.Modified, Scenario.Corrupted];
        foreach (Scenario scenario in scenarios)
        {
            Input input = GetInput(vector, scenario);
            OpusCompare.Result managed = GetMetric(vector, scenario);
            LibOpusOracle.Comparison native = LibOpusOracle.Compare(input.Reference, input.Test, Rate, stereo: true);
            _output.WriteLine(FormattableString.Invariant(
                $"vector {vector:D2} {scenario}: managed {managed.Quality:F4} %, native {native.Quality:F4} %"));

            native.Passed.Should().Be(
                managed.Passed,
                FormattableString.Invariant(
                    $"vector {vector:D2}, {scenario}: managed quality {managed.Quality:F4} %, native quality {native.Quality:F4} %"));
            native.Quality.Should().BeApproximately(
                managed.Quality,
                0.01,
                FormattableString.Invariant(
                    $"vector {vector:D2}, {scenario}: managed error {managed.Error:G6}, native error {native.Error:G6}"));
        }
    }

    /// <summary>(d) The pinned oracle decoder reproduces a conformant reference decode.</summary>
    /// <param name="vector">Vector number.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Oracle_decoder_reproduces_the_reference(int vector)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        OpusCompare.Result againstReference = GetMetric(vector, Scenario.Decoded);
        if (againstReference.Passed)
        {
            _output.WriteLine(FormattableString.Invariant(
                $"vector {vector:D2}: decoded vs .dec {againstReference.Quality:F4} %"));
            return;
        }

        // RFC 8251 makes both reference decodes conformant, so matching either one suffices.
        short[] decoded = GetInput(vector, Scenario.Decoded).Test;
        short[] modified = Pcm16.Read(TestVectors.ModifiedDecPath(vector));
        OpusCompare.Result againstModified = OpusCompare.Compare(modified, decoded, Rate, stereo: true);
        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2}: decoded vs .dec {againstReference.Quality:F4} %, vs m.dec {againstModified.Quality:F4} %"));
        againstModified.Passed.Should().BeTrue(
            FormattableString.Invariant(
                $"vector {vector:D2}: oracle decode must match .dec or m.dec; against .dec quality {againstReference.Quality:F4} %, against m.dec quality {againstModified.Quality:F4} %"));
    }

    private static Input GetInput(int vector, Scenario scenario) =>
        Inputs.GetOrAdd(
            (vector, scenario),
            key => new Lazy<Input>(() => BuildInput(key.Vector, key.Scenario))).Value;

    private static OpusCompare.Result GetMetric(int vector, Scenario scenario) =>
        Metrics.GetOrAdd(
            (vector, scenario),
            key =>
            {
                Input input = GetInput(key.Vector, key.Scenario);
                return new Lazy<OpusCompare.Result>(
                    () => OpusCompare.Compare(input.Reference, input.Test, Rate, stereo: true));
            }).Value;

    private static Input BuildInput(int vector, Scenario scenario)
    {
        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        short[] test = scenario switch
        {
            Scenario.Self => reference,
            Scenario.Modified => Pcm16.Read(TestVectors.ModifiedDecPath(vector)),
            Scenario.Corrupted => Corrupt(reference),
            Scenario.Decoded => LibOpusOracle.Decode(TestVectors.BitPath(vector), Rate, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario."),
        };

        return new Input(reference, test);
    }

    private static short[] Corrupt(short[] reference)
    {
        short[] corrupted = (short[])reference.Clone();
        int frames = reference.Length / 2;
        const int silentFrames = Rate / 50; // 20 ms
        const int periodFrames = Rate / 5; // 200 ms
        for (int start = 0; start + silentFrames <= frames; start += periodFrames)
        {
            Array.Clear(corrupted, start * 2, silentFrames * 2);
        }

        return corrupted;
    }

    private static string Describe(int vector, string pair, OpusCompare.Result result) =>
        FormattableString.Invariant(
            $"vector {vector:D2}, {pair}: quality {result.Quality:F4} %, error {result.Error:G6}");

    private sealed record Input(short[] Reference, short[] Test);
}
