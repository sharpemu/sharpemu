// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public interface IGpuCommandBufferSource
{
    nint[] AllocateBuffers(int count);

    void BeginBuffer(nint buffer);

    void EndBuffer(nint buffer, bool hostRead);
}

// Provide the scheduler with one timeline semaphore, one queue, its own command pool and further
// command pools for other recording threads.
public interface IGpuTickDevice : IGpuCommandBufferSource, IDisposable
{
    object QueueGate { get; }

    ulong TimelineHandle { get; }

    ulong ReadTimeline();

    bool TryWaitTimeline(ulong tick, out string failure);

    IGpuCommandBufferSource CreateBufferSource();

    bool TrySubmit(ReadOnlySpan<nint> buffers, SubmitBundle bundle, out string failure);
}
