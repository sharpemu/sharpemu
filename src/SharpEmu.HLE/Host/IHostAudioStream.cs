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

    /// <summary>
    /// Audio already handed to the device and not yet played, in milliseconds —
    /// the cushion protecting playback from a late submission. Zero means the
    /// device has run dry and is emitting silence.
    ///
    /// Callers that pace the guest against an emulated hardware queue need this:
    /// pacing purely on wall clock releases exactly one buffer per buffer-period
    /// and so keeps the cushion at zero, which turns any scheduling jitter into
    /// an audible dropout. Returns -1 when the backend cannot report a depth, in
    /// which case callers must fall back to their own pacing.
    /// </summary>
    int QueuedMilliseconds => -1;
}
