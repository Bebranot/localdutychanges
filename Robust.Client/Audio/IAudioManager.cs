using System;
using System.IO;
using Robust.Shared.Audio.Sources;

namespace Robust.Client.Audio;

/// <summary>
/// Public audio API for stuff that can't go through <see cref="AudioSystem"/>
/// </summary>
[NotContentImplementable]
public interface IAudioManager
{
    IAudioSource? CreateAudioSource(AudioStream stream);

    AudioStream LoadAudioOggVorbis(Stream stream, string? name = null);

    /// <summary>
    /// Decodes an ogg vorbis stream into raw PCM without touching OpenAL.
    /// </summary>
    /// <remarks>
    /// Safe to call from a background thread. Pass the result to <see cref="LoadAudioRaw"/> on the
    /// main thread to actually get a playable <see cref="AudioStream"/>. Use this instead of
    /// <see cref="LoadAudioOggVorbis"/> when the decode cost would otherwise stall the frame,
    /// e.g. for multi-minute music tracks.
    /// </remarks>
    AudioPcmData DecodeAudioOggVorbis(Stream stream);

    AudioStream LoadAudioWav(Stream stream, string? name = null);

    AudioStream LoadAudioRaw(ReadOnlySpan<short> samples, int channels, int sampleRate, string? name = null);

    void SetMasterGain(float gain);
}
