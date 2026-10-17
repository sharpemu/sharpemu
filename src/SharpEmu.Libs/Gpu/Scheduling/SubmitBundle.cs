// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class SubmitBundle
{
    public const int MaxSemaphores = 3;
    public const uint AllCommandsStage = 0x0001_0000;

    public readonly ulong[] WaitSemaphores = new ulong[MaxSemaphores];
    public readonly ulong[] WaitTicks = new ulong[MaxSemaphores];
    public readonly uint[] WaitStages = new uint[MaxSemaphores];
    public readonly ulong[] SignalSemaphores = new ulong[MaxSemaphores];
    public readonly ulong[] SignalTicks = new ulong[MaxSemaphores];

    public SubmitBundle()
    {
    }

    public SubmitBundle(SubmitBundle source)
    {
        source.WaitSemaphores.CopyTo(WaitSemaphores, 0);
        source.WaitTicks.CopyTo(WaitTicks, 0);
        source.WaitStages.CopyTo(WaitStages, 0);
        source.SignalSemaphores.CopyTo(SignalSemaphores, 0);
        source.SignalTicks.CopyTo(SignalTicks, 0);
        WaitCount = source.WaitCount;
        SignalCount = source.SignalCount;
    }

    public int WaitCount { get; private set; }

    public int SignalCount { get; private set; }

    public void AddWait(ulong semaphore, ulong tick = 1, uint stage = AllCommandsStage)
    {
        if (semaphore == 0 || WaitCount >= MaxSemaphores)
        {
            throw SubmissionScheduler.Fatal($"Cannot add the submission wait: semaphore=0x{semaphore:X} count={WaitCount}");
        }

        WaitSemaphores[WaitCount] = semaphore;
        WaitTicks[WaitCount] = tick;
        WaitStages[WaitCount++] = stage;
    }

    public void AddSignal(ulong semaphore, ulong tick = 1)
    {
        if (semaphore == 0 || SignalCount >= MaxSemaphores)
        {
            throw SubmissionScheduler.Fatal($"Cannot add the submission signal: semaphore=0x{semaphore:X} count={SignalCount}");
        }

        SignalSemaphores[SignalCount] = semaphore;
        SignalTicks[SignalCount++] = tick;
    }
}
