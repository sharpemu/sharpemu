// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// POSIX audio output: CoreAudio (AudioQueue) on macOS, ALSA on Linux. Both
/// streams accept the seam's interleaved stereo PCM16 and pace the guest via
/// device-queue backpressure.
/// </summary>
internal sealed class PosixHostAudio : IHostAudioOutput
{
    public string BackendName => OperatingSystem.IsMacOS() ? "coreaudio" : "alsa";

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate)
    {
        return OperatingSystem.IsMacOS()
            ? new PosixCoreAudioStream(sampleRate)
            : new PosixAlsaAudioStream(sampleRate);
    }
}
