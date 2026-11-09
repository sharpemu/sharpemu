// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public interface IFlipSubmitter
{
    const int FlipQueueFull = unchecked((int)0x80290012);

    int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId);

    void WaitForSubmitSlot();

    void CompleteFlip(ulong requestId);
}
