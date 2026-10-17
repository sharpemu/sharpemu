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
}

public enum HostPcmFormat
{
    Signed16,
    Float32,
}
