// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Host audio sink for a single <c>sceAudioOut</c> port. Implementations
/// receive the guest's raw sample frames and are responsible for converting
/// and delivering them to the operating system's audio device.
/// </summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>
    /// Submits one block of guest audio for playback.
    /// </summary>
    /// <param name="source">Interleaved guest samples for <paramref name="frames"/> frames.</param>
    /// <param name="frames">Number of frames (one sample per channel per frame).</param>
    /// <param name="channels">Channels per frame in <paramref name="source"/>.</param>
    /// <param name="bytesPerSample">Bytes per sample in <paramref name="source"/> (2 or 4).</param>
    /// <param name="isFloat">Whether <paramref name="source"/> holds 32-bit floats.</param>
    /// <returns>
    /// <see langword="true"/> if the block was accepted and paced by the
    /// backend; <see langword="false"/> if the caller should pace silence
    /// itself (e.g. the device is unavailable).
    /// </returns>
    bool Submit(
        ReadOnlySpan<byte> source,
        uint frames,
        int channels,
        int bytesPerSample,
        bool isFloat);
}
