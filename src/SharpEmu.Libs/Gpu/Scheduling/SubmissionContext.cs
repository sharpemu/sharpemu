// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// The guest queue whose work the current recording buffer carries.
public sealed class SubmissionContext
{
    public string QueueName { get; set; } = "host.default";

    public ulong SubmissionId { get; set; }
}
