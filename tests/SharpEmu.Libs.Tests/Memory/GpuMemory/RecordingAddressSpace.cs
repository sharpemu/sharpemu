// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

internal sealed class RecordingAddressSpace : IGuestAddressSpace
{
    public List<(ulong Address, ulong Size, GuestPageProtection Protection)> Protects { get; } = new();

    public bool FailProtect { get; set; }

    public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
    {
        Protects.Add((address, size, protection));
        return !FailProtect;
    }

    public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();

    public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();

    public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();

    public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();

    public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
}

[CollectionDefinition(GpuMemoryStateCollection.Name, DisableParallelization = true)]
public sealed class GpuMemoryStateCollection
{
    public const string Name = "GpuMemoryState";
}
