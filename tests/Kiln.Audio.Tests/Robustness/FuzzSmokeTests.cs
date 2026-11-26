using System.Diagnostics;
using FluentAssertions;
using Kiln.Audio.Fuzz;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// The CI-friendly slice of the fuzzer: replay every committed RFC 8251 packet, then run 20,000
/// deterministic mutations, inside a 30-second budget.
/// </summary>
/// <remarks>
/// The long sessions are the standalone <c>Kiln.Audio.Fuzz</c> executable; this test uses the
/// same engine and a fixed seed, so a finding here is reproducible with the executable:
/// <c>--seed 1179666522 --executions ...</c>. The seed is <c>FUZZ</c> in hex.
/// </remarks>
public class FuzzSmokeTests
{
    [Fact]
    public void Corpus_and_20000_mutations_stay_within_the_contract_and_the_time_budget()
    {
        var corpus = new FuzzCorpus();
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            corpus.LoadBitFile(TestVectors.BitPath(vector));
        }

        var options = new FuzzOptions
        {
            Seed = 0x46555A5A,
            Mutations = 20000,
            ReplayCorpus = true,
            CheckAllocations = true,
            IncludeEncoder = true,
        };

        var stopwatch = Stopwatch.StartNew();
        FuzzReport report = FuzzEngine.Run(corpus, options);
        stopwatch.Stop();

        string failures = string.Join(
            Environment.NewLine,
            report.Findings.Select(finding => FormattableString.Invariant($"{finding.Target}: {finding.Message}")));
        report.Findings.Should().BeEmpty(failures);
        report.Seeds.Should().BeGreaterThan(1000);
        report.Mutations.Should().Be(20000);
        report.AllocationChecks.Should().BeGreaterThan(0);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }
}
