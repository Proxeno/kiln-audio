using System.Diagnostics;
using System.IO;

namespace Kiln.Audio.Fuzz;

/// <summary>How one fuzz session should run.</summary>
internal sealed class FuzzOptions
{
    /// <summary>Gets the session seed; the same seed replays the same mutation sequence.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Gets the execution cap, or <see cref="long.MaxValue"/> for no cap.</summary>
    public long MaxExecutions { get; init; } = long.MaxValue;

    /// <summary>Gets the mutation cap, excluding the corpus replay, or <see cref="long.MaxValue"/>.</summary>
    public long Mutations { get; init; } = long.MaxValue;

    /// <summary>Gets the wall-clock cap, or <see cref="TimeSpan.Zero"/> for no cap.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Gets the directory findings are written to, or null to keep them in memory only.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Gets a value indicating whether the encoder target runs.</summary>
    public bool IncludeEncoder { get; init; } = true;

    /// <summary>Gets a value indicating whether the zero-allocation contract is measured first.</summary>
    public bool CheckAllocations { get; init; } = true;

    /// <summary>Gets a value indicating whether every seed packet is replayed before mutation starts.</summary>
    public bool ReplayCorpus { get; init; } = true;

    /// <summary>Gets the progress sink, or null for silence.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>The tally of one fuzz session.</summary>
/// <param name="Executions">Total target invocations, including the corpus replay.</param>
/// <param name="Mutations">Mutated executions, excluding the corpus replay.</param>
/// <param name="ParserExecutions">Parser invocations.</param>
/// <param name="DecoderExecutions">Decoder invocations.</param>
/// <param name="EncoderExecutions">Encoder invocations.</param>
/// <param name="Seeds">Number of seed packets in the corpus.</param>
/// <param name="AllocationChecks">Number of valid packets measured for zero allocation.</param>
/// <param name="Elapsed">Wall-clock time the session ran.</param>
/// <param name="Findings">Distinct contract violations found.</param>
internal sealed record FuzzReport(
    long Executions,
    long Mutations,
    long ParserExecutions,
    long DecoderExecutions,
    long EncoderExecutions,
    int Seeds,
    int AllocationChecks,
    TimeSpan Elapsed,
    IReadOnlyList<FuzzFinding> Findings);

/// <summary>
/// The deterministic mutational fuzzer: picks a seed packet, mutates it, and runs one of the
/// targets under the resilience contract until the execution or time cap is reached.
/// </summary>
/// <remarks>
/// The harness is single-threaded by design. Coverage-guided tooling (SharpFuzz and libFuzzer)
/// needs a native instrumentation driver; this harness trades guidance for a stable seed, no
/// native dependency, and a corpus that runs on any machine. Structure-aware operators (TOC
/// sweeps, length-field writes, code 3 count and padding mutations) bias the mutations toward
/// valid framing, which is where the decoder's deep paths live.
/// </remarks>
internal static class FuzzEngine
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    /// <summary>Runs one session.</summary>
    /// <param name="corpus">The seed packets; must not be empty.</param>
    /// <param name="options">Session configuration.</param>
    /// <returns>The session tally.</returns>
    /// <exception cref="ArgumentException">The corpus is empty.</exception>
    internal static FuzzReport Run(FuzzCorpus corpus, FuzzOptions options)
    {
        if (corpus.Packets.Count == 0)
        {
            throw new ArgumentException("The fuzz corpus is empty.", nameof(corpus));
        }

        var rng = new FuzzRng(options.Seed);
        var findings = new List<FuzzFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        double lastProgress = 0;
        long parser = 0;
        long decoder = 0;
        long encoder = 0;
        long executions = 0;
        long mutations = 0;
        int allocationChecks = 0;

        using var decoderTarget = new DecoderTarget();
        using var encoderTarget = new EncoderTarget();

        if (options.CheckAllocations)
        {
            try
            {
                allocationChecks = DecoderTarget.CheckAllocations(corpus.Packets);
            }
            catch (FuzzFailureException failure)
            {
                Record(failure, executions, options, findings, seen);
                decoderTarget.Reset();
            }
        }

        if (options.ReplayCorpus)
        {
            foreach (byte[] packet in corpus.Packets)
            {
                try
                {
                    ParserTarget.Run(packet, rng);
                    parser++;
                }
                catch (FuzzFailureException failure)
                {
                    Record(failure, executions, options, findings, seen);
                }

                try
                {
                    decoderTarget.Run(packet, rng);
                    decoder++;
                }
                catch (FuzzFailureException failure)
                {
                    Record(failure, executions, options, findings, seen);
                    decoderTarget.Reset();
                }

                executions += 2;
            }
        }

        while (mutations < options.Mutations
            && executions < options.MaxExecutions
            && (options.MaxDuration == TimeSpan.Zero || stopwatch.Elapsed < options.MaxDuration))
        {
            if ((executions & 0xFF) == 0
                && options.MaxDuration != TimeSpan.Zero
                && stopwatch.Elapsed >= options.MaxDuration)
            {
                break;
            }

            FuzzTargetKind kind = ChooseTarget(rng, options.IncludeEncoder);
            byte[] packet = rng.Chance(1, 10)
                ? Mutator.RandomPacket(rng)
                : Mutator.Mutate(corpus.Packets[rng.Next(corpus.Packets.Count)], rng);

            try
            {
                switch (kind)
                {
                    case FuzzTargetKind.Parser:
                        ParserTarget.Run(packet, rng);
                        parser++;
                        break;
                    case FuzzTargetKind.Decoder:
                        decoderTarget.Run(packet, rng);
                        decoder++;
                        break;
                    default:
                        encoderTarget.Run(rng);
                        encoder++;
                        break;
                }
            }
            catch (FuzzFailureException failure)
            {
                Record(failure, executions, options, findings, seen);
                decoderTarget.Reset();
                encoderTarget.Reset();
            }
            catch (OutOfMemoryException exception)
            {
                Record(
                    new FuzzFailureException(kind, packet, "The target ran out of memory.", exception),
                    executions,
                    options,
                    findings,
                    seen);
                break;
            }
            catch (Exception exception)
            {
                Record(
                    new FuzzFailureException(
                        kind,
                        packet,
                        FormattableString.Invariant($"The target threw an unwrapped {exception.GetType().Name}: {exception.Message}."),
                        exception),
                    executions,
                    options,
                    findings,
                    seen);
                decoderTarget.Reset();
                encoderTarget.Reset();
            }

            executions++;
            mutations++;

            if (options.Log is not null && stopwatch.Elapsed.TotalSeconds - lastProgress >= ProgressInterval.TotalSeconds)
            {
                lastProgress = stopwatch.Elapsed.TotalSeconds;
                options.Log(FormattableString.Invariant(
                    $"{executions} execs, {executions / Math.Max(1, stopwatch.Elapsed.TotalSeconds):0} execs/s, {findings.Count} findings"));
            }
        }

        stopwatch.Stop();
        options.Log?.Invoke(FormattableString.Invariant(
            $"done: {executions} execs in {stopwatch.Elapsed.TotalSeconds:0.0} s, {findings.Count} findings"));
        return new FuzzReport(executions, mutations, parser, decoder, encoder, corpus.Packets.Count, allocationChecks, stopwatch.Elapsed, findings);
    }

    /// <summary>Runs the parser target once on <paramref name="packet"/>, for reproducing a finding.</summary>
    /// <param name="packet">The input.</param>
    /// <returns>True when a contract violation reproduces.</returns>
    internal static bool ReproducesParser(byte[] packet)
    {
        try
        {
            ParserTarget.Run(packet, new FuzzRng(0x5EED));
            return false;
        }
        catch (FuzzFailureException)
        {
            return true;
        }
    }

    private static FuzzTargetKind ChooseTarget(FuzzRng rng, bool includeEncoder)
    {
        int roll = rng.Next(100);
        if (!includeEncoder)
        {
            return roll < 34 ? FuzzTargetKind.Parser : FuzzTargetKind.Decoder;
        }

        return roll switch
        {
            < 25 => FuzzTargetKind.Parser,
            < 80 => FuzzTargetKind.Decoder,
            _ => FuzzTargetKind.Encoder,
        };
    }

    private static void Record(
        FuzzFailureException failure,
        long execution,
        FuzzOptions options,
        List<FuzzFinding> findings,
        HashSet<string> seen)
    {
        // The exception type and message alone do not distinguish two different bounds bugs;
        // the first stack frame does.
        string? frame = failure.InnerException?.StackTrace?.Split('\n')[0].Trim();
        string signature = FormattableString.Invariant($"{failure.Target}|{failure.Message}|{frame}");
        if (!seen.Add(signature))
        {
            return;
        }

        (byte[] minimized, IReadOnlyList<FuzzStep>? sequence) = Minimize(failure);
        var finding = new FuzzFinding(
            failure.Target,
            minimized,
            failure.Message,
            execution,
            failure.SampleRate,
            failure.Channels,
            failure.DecodeFec,
            failure.UseFloat,
            sequence);
        findings.Add(finding);
        options.Log?.Invoke(FormattableString.Invariant(
            $"FINDING {failure.Target}: {failure.Message} ({(sequence is null ? $"{minimized.Length} bytes" : $"{sequence.Count} steps")})"));

        if (options.OutputDirectory is not null)
        {
            Save(finding, options.OutputDirectory);
        }
    }

    private static (byte[] Packet, IReadOnlyList<FuzzStep>? Sequence) Minimize(FuzzFailureException failure)
    {
        byte[] packet = failure.Packet;
        try
        {
            if (failure.Target == FuzzTargetKind.Parser && packet.Length > 0)
            {
                return (Minimizer.Minimize(packet, ReproducesParser), null);
            }

            if (failure.Target != FuzzTargetKind.Decoder || failure.SampleRate == 0)
            {
                return (packet, null);
            }

            // Prefer a single-packet repro; fall back to the recorded decoder history when the
            // failure depends on cross-packet state.
            if (packet.Length > 0 && DecoderTarget.Reproduces(packet, failure.SampleRate, failure.Channels, failure.DecodeFec, failure.UseFloat))
            {
                byte[] single = Minimizer.Minimize(
                    packet,
                    candidate => DecoderTarget.Reproduces(candidate, failure.SampleRate, failure.Channels, failure.DecodeFec, failure.UseFloat));
                return (single, null);
            }

            if (failure.Sequence is { Count: > 0 } sequence && DecoderTarget.ReproducesSequence(sequence))
            {
                IReadOnlyList<FuzzStep> minimized = MinimizeSequence(sequence);
                return (minimized[^1].Packet, minimized);
            }
        }
        catch (Exception)
        {
            // Minimization is best-effort; the original input is always recorded.
        }

        // Keep the recorded history even when nothing shrank, so a state-dependent finding
        // stays replayable instead of being saved as a lone packet that does not reproduce.
        return (packet, failure.Sequence);
    }

    private static IReadOnlyList<FuzzStep> MinimizeSequence(IReadOnlyList<FuzzStep> sequence)
    {
        var steps = new List<FuzzStep>(sequence);

        // Drop leading steps while the failure survives; then drop any other non-final step.
        while (steps.Count > 1)
        {
            var candidate = steps.GetRange(1, steps.Count - 1);
            if (!DecoderTarget.ReproducesSequence(candidate))
            {
                break;
            }

            steps = candidate;
        }

        for (int i = steps.Count - 2; i >= 0; i--)
        {
            var candidate = new List<FuzzStep>(steps);
            candidate.RemoveAt(i);
            if (DecoderTarget.ReproducesSequence(candidate))
            {
                steps = candidate;
            }
        }

        int last = steps.Count - 1;
        byte[] minimized = Minimizer.Minimize(
            steps[last].Packet,
            candidate =>
            {
                var attempt = new List<FuzzStep>(steps);
                attempt[last] = steps[last] with { Packet = candidate };
                return DecoderTarget.ReproducesSequence(attempt);
            },
            maxEvaluations: 120);
        steps[last] = steps[last] with { Packet = minimized };
        return steps;
    }

    private static void Save(FuzzFinding finding, string directory)
    {
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, FormattableString.Invariant($"{finding.Target.ToString().ToLowerInvariant()}-{finding.Hash}"));
        if (finding.Sequence is { } sequence)
        {
            FuzzSequenceFile.Write(stem + ".seq", sequence);
        }
        else
        {
            File.WriteAllBytes(stem + ".bin", finding.Packet);
        }

        File.WriteAllText(
            stem + ".txt",
            FormattableString.Invariant(
                $"target: {finding.Target}\nrate: {finding.SampleRate}\nchannels: {finding.Channels}\nfec: {finding.DecodeFec}\nfloat: {finding.UseFloat}\nsteps: {finding.Sequence?.Count ?? 0}\nmessage: {finding.Message}\nhex: {finding.Hex}\n"));
    }
}
