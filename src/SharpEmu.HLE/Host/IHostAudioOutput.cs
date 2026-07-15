// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host audio-output device access. The HLE audio exports convert guest submissions to
/// interleaved stereo 16-bit PCM (the format every backend accepts) and feed them through
/// streams opened here; everything device-specific — queueing, backpressure, native
/// buffer lifetime — lives behind <see cref="IHostAudioStream"/>.
/// </summary>
public interface IHostAudioOutput
{
    /// <summary>Backend identifier for diagnostics (e.g. "winmm").</summary>
    string BackendName { get; }

    /// <summary>
    /// Opens an interleaved stereo 16-bit PCM output stream at the given sample rate.
    /// Throws when the host has no usable output device; callers degrade to a silent
    /// port and pace the guest instead.
    /// </summary>
    IHostAudioStream OpenStereoPcm16Stream(uint sampleRate);
}
