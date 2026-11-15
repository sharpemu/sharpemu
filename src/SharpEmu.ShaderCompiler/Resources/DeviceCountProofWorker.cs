// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.ShaderCompiler.Resources;

// One worker across plans. A dispatch never waits for admission or completion; it keeps the exact count path.
internal sealed class DeviceCountProofWorker
{
    private static readonly Lazy<DeviceCountProofWorker> Instance = new(() => new());
    private readonly BlockingCollection<Action> _jobs = new(16);

    private DeviceCountProofWorker()
    {
        new Thread(Run) { IsBackground = true, Name = "Device count proofs" }.Start();
    }

    internal static bool TryQueue(Action job) => Instance.Value._jobs.TryAdd(job);

    private void Run()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            // Failed optional analysis leaves the cached refusal and ordinary count path intact.
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }
}
