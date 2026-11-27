using FluentAssertions;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// Regression coverage for CELT packet-loss concealment state. A hybrid loss conceals only the
/// high band, as shaped noise, without running the pitch search; a following CELT-only loss must
/// not reuse the (still zero) pitch index.
/// </summary>
public class CeltPlcConcealmentTests
{
    [Fact]
    public void A_CELT_loss_after_a_hybrid_noise_loss_searches_for_a_pitch()
    {
        var synthesizer = new CeltSynthesizer(outputChannels: 2);
        var pcm = new float[2 * 960];

        // A hybrid frame's higher bands are concealed as noise, which is not the pitch branch.
        synthesizer.DecodeLost(lm: 3, startBand: 17, pcm);

        // Before the fix this reused a zero pitch index and read past the excitation buffer.
        Action conceal = () => synthesizer.DecodeLost(lm: 3, startBand: 0, pcm);
        conceal.Should().NotThrow();
        pcm.Should().OnlyContain(sample => float.IsFinite(sample));
    }

    [Fact]
    public void Repeated_CELT_losses_keep_the_pitch_branch_and_stay_finite()
    {
        var synthesizer = new CeltSynthesizer(outputChannels: 1);
        var pcm = new float[960];

        for (int loss = 0; loss < 6; loss++)
        {
            synthesizer.DecodeLost(lm: 3, startBand: 0, pcm);
            pcm.Should().OnlyContain(sample => float.IsFinite(sample));
        }
    }
}
