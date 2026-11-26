namespace Kiln.Audio.Silk;

/// <summary>
/// Caller-owned storage for the parameters decoded from one Opus frame's SILK layer.
/// </summary>
/// <remarks>
/// <para>
/// One instance is reused for every frame of a stream; <see cref="SilkDecoder.DecodeFrame"/>
/// clears and refills it without allocating. Frames are indexed by their 20 ms (or 10 ms)
/// interval within the Opus frame, and channels by 0 for mid (or mono) and 1 for side.
/// </para>
/// <para>
/// The LBRR frames are kept in their own set of slots, because they describe the prior Opus
/// frame's audio and a later in-band FEC implementation needs both at once.
/// </para>
/// </remarks>
internal sealed class SilkFrameResult
{
    /// <summary>Maximum number of 20 ms SILK frames in one Opus frame.</summary>
    internal const int MaxSilkFrames = 3;

    /// <summary>Maximum number of channels in a SILK layer.</summary>
    internal const int MaxChannels = 2;

    private readonly SilkChannelFrame[,] _regular = CreateFrames();
    private readonly SilkChannelFrame[,] _lbrr = CreateFrames();
    private readonly bool[,] _regularCoded = new bool[MaxSilkFrames, MaxChannels];
    private readonly bool[,] _lbrrCoded = new bool[MaxSilkFrames, MaxChannels];

    /// <summary>Gets the internal sample rate of the decoded frames, in Hz.</summary>
    internal int InternalRateHz { get; private set; }

    /// <summary>Gets the number of channels in the SILK layer: 1 for mono, 2 for stereo.</summary>
    internal int Channels { get; private set; }

    /// <summary>Gets the duration of the Opus frame in milliseconds: 10, 20, 40 or 60.</summary>
    internal int FrameDurationMs { get; private set; }

    /// <summary>Gets the duration of each SILK frame in milliseconds: 10 or 20.</summary>
    internal int SilkFrameDurationMs { get; private set; }

    /// <summary>Gets the number of SILK frames per channel in the Opus frame: 1 for 10 and 20 ms, up to 3 otherwise.</summary>
    internal int SilkFrameCount { get; private set; }

    /// <summary>Gets the number of 5 ms subframes in one SILK frame: 2 for 10 ms, 4 for 20 ms.</summary>
    internal int SubframeCount { get; private set; }

    /// <summary>Returns the decoded parameters of a regular SILK frame.</summary>
    /// <param name="frame">Interval index, 0 to <see cref="SilkFrameCount"/> - 1.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal SilkChannelFrame Regular(int frame, int channel) => _regular[frame, channel];

    /// <summary>Returns the decoded parameters of an LBRR frame.</summary>
    /// <param name="frame">Interval index, 0 to <see cref="SilkFrameCount"/> - 1.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal SilkChannelFrame Lbrr(int frame, int channel) => _lbrr[frame, channel];

    /// <summary>Returns whether a regular SILK frame was coded for an interval and channel.</summary>
    /// <param name="frame">Interval index.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal bool IsRegularCoded(int frame, int channel) => _regularCoded[frame, channel];

    /// <summary>Returns whether an LBRR frame was coded for an interval and channel.</summary>
    /// <param name="frame">Interval index.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal bool IsLbrrCoded(int frame, int channel) => _lbrrCoded[frame, channel];

    /// <summary>Gets the VAD flag of a regular SILK frame.</summary>
    /// <param name="frame">Interval index.</param>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal bool VadFlag(int frame, int channel) => _vadFlags[frame, channel];

    /// <summary>Gets whether the whole channel had its LBRR flag set.</summary>
    /// <param name="channel">Channel: 0 mid or mono, 1 side.</param>
    internal bool HasLbrr(int channel) => _hasLbrr[channel];

    private readonly bool[,] _vadFlags = new bool[MaxSilkFrames, MaxChannels];
    private readonly bool[] _hasLbrr = new bool[MaxChannels];

    /// <summary>Clears the result and describes the frames that are about to be decoded.</summary>
    /// <param name="internalRateHz">Internal sample rate in Hz.</param>
    /// <param name="channels">Channel count: 1 or 2.</param>
    /// <param name="frameDurationMs">Opus frame duration in milliseconds.</param>
    internal void Begin(int internalRateHz, int channels, int frameDurationMs)
    {
        InternalRateHz = internalRateHz;
        Channels = channels;
        FrameDurationMs = frameDurationMs;
        SilkFrameDurationMs = frameDurationMs == 10 ? 10 : 20;
        SilkFrameCount = frameDurationMs == 10 ? 1 : frameDurationMs / 20;
        SubframeCount = SilkFrameDurationMs == 10 ? 2 : 4;

        for (int frame = 0; frame < MaxSilkFrames; frame++)
        {
            for (int channel = 0; channel < MaxChannels; channel++)
            {
                _regularCoded[frame, channel] = false;
                _lbrrCoded[frame, channel] = false;
                _vadFlags[frame, channel] = false;
                if (frame < SilkFrameCount && channel < channels)
                {
                    _regular[frame, channel].Clear();
                    _lbrr[frame, channel].Clear();
                }
            }
        }

        _hasLbrr[0] = false;
        _hasLbrr[1] = false;
    }

    /// <summary>Marks a regular frame as present and returns its storage.</summary>
    internal SilkChannelFrame BeginRegular(int frame, int channel)
    {
        _regularCoded[frame, channel] = true;
        return _regular[frame, channel];
    }

    /// <summary>Marks an LBRR frame as present and returns its storage.</summary>
    internal SilkChannelFrame BeginLbrr(int frame, int channel)
    {
        _lbrrCoded[frame, channel] = true;
        return _lbrr[frame, channel];
    }

    /// <summary>Records a VAD flag read from the header bits.</summary>
    internal void SetVadFlag(int frame, int channel, bool value) => _vadFlags[frame, channel] = value;

    /// <summary>Records that a channel's global LBRR flag was set.</summary>
    internal void SetHasLbrr(int channel, bool value) => _hasLbrr[channel] = value;

    private static SilkChannelFrame[,] CreateFrames()
    {
        var frames = new SilkChannelFrame[MaxSilkFrames, MaxChannels];
        for (int frame = 0; frame < MaxSilkFrames; frame++)
        {
            for (int channel = 0; channel < MaxChannels; channel++)
            {
                frames[frame, channel] = new SilkChannelFrame();
            }
        }

        return frames;
    }
}
