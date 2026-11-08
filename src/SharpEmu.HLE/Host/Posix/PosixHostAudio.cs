// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// POSIX audio output through ALSA. The stream accepts the seam's interleaved
/// stereo PCM16 and paces the guest via device-queue backpressure.
/// </summary>
internal sealed class PosixHostAudio : IHostAudioOutput
{
    public string BackendName => "alsa";

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024)
    {
        return new PosixAlsaAudioStream(sampleRate, maxQueuedPcmBytes);
    }
}
