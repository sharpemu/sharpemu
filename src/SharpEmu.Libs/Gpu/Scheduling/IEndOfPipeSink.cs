// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public interface IEndOfPipeSink
{
    void TriggerInterrupt(int eventId, uint contextId);
}
