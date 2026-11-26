namespace Kiln.Audio;

/// <summary>
/// Opus frame durations (RFC 6716 §2.1.4, Table 2). Not every duration is legal in every mode:
/// 2.5 ms and 5 ms are CELT-only; 40 ms and 60 ms are SILK-only.
/// </summary>
public enum OpusFrameDuration
{
    /// <summary>2.5 milliseconds.</summary>
    Ms2_5 = 0,

    /// <summary>5 milliseconds.</summary>
    Ms5 = 1,

    /// <summary>10 milliseconds.</summary>
    Ms10 = 2,

    /// <summary>20 milliseconds.</summary>
    Ms20 = 3,

    /// <summary>40 milliseconds.</summary>
    Ms40 = 4,

    /// <summary>60 milliseconds.</summary>
    Ms60 = 5,
}
