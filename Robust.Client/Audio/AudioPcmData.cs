using System;

namespace Robust.Client.Audio;

/// <summary>
/// Raw decoded PCM audio produced by <see cref="IAudioManager.DecodeAudioOggVorbis"/>.
/// </summary>
/// <remarks>
/// Holds no OpenAL (or any other graphics/audio driver) resources, which is the whole point:
/// decoding is the expensive part and can be done on a background thread, while only the cheap
/// upload via <see cref="IAudioManager.LoadAudioRaw"/> has to happen on the main thread.
/// </remarks>
public sealed class AudioPcmData
{
    /// <summary>16-bit interleaved samples.</summary>
    public ReadOnlyMemory<short> Samples { get; }

    /// <summary>Channel count. Only 1 (mono) and 2 (stereo) can be uploaded.</summary>
    public int Channels { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    public AudioPcmData(ReadOnlyMemory<short> samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }
}
