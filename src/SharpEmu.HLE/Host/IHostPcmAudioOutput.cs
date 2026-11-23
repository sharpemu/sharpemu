// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Optional host-audio extension for backends that can accept the guest's
/// interleaved PCM layout directly and perform device conversion themselves.
/// </summary>
public interface IHostPcmAudioOutput : IHostAudioOutput
{
    IHostAudioStream OpenPcmStream(uint sampleRate, int channels, HostPcmFormat format);

    /// <summary>
    /// The same stream with a caller-chosen backpressure cap in bytes, for callers that pace the guest themselves and
    /// need a deeper queue than the default.
    /// </summary>
    IHostAudioStream OpenPcmStream(uint sampleRate, int channels, HostPcmFormat format, int maxQueuedBytes) =>
        OpenPcmStream(sampleRate, channels, format);

    /// <summary>
    /// A float stream on the game controller's own output device, or null when the host has none. A DualSense on USB
    /// lists one with four channels: its speaker (or a headset plugged into the controller) on the first two and its
    /// two haptic actuators on the last two. <paramref name="channels"/> is 4 when the device takes all of them and 2
    /// when it takes only the speaker pair. The stream does not report the guest audio clock. Without
    /// <paramref name="routeSpeaker"/> the controller's speaker routing is left alone (a stream for the actuators).
    /// </summary>
    IHostAudioStream? TryOpenControllerStream(uint sampleRate, int maxQueuedBytes, out int channels, bool routeSpeaker = true)
    {
        channels = 0;
        return null;
    }
}

public enum HostPcmFormat
{
    Signed16,
    Float32,
}
