// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Gpu.Scheduling;

// Delivers a GPU interrupt to every queue that registered the event id; one entry per trigger.
public sealed class EndOfPipeEvents : IEndOfPipeSink
{
    public void TriggerInterrupt(int eventId, uint contextId) =>
        _ = KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            contextId,
            unchecked((ulong)eventId));
}
