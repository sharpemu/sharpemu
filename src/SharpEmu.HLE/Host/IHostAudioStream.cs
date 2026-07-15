// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// One open host audio output stream. Submissions are interleaved stereo 16-bit PCM at
/// the sample rate the stream was opened with.
/// </summary>
public interface IHostAudioStream : IDisposable
{
    /// <summary>
    /// Submits one buffer. May block briefly while the device drains its queue (this is
    /// what paces the guest's audio loop); returns false when the stream cannot accept
    /// audio, in which case the caller paces the guest itself.
    /// </summary>
    bool Submit(ReadOnlySpan<byte> stereoPcm16);
}
