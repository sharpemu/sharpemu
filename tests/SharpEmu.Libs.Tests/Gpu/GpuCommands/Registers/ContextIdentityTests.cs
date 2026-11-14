// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// The direct executor path can receive live banks. Retaining a bare Context reference
// does not create a read-only snapshot or establish the producer's COW obligation.
public sealed class ContextIdentityTests
{
    private static RegisterBanks NewBanks() =>
        new(static message => new InvalidOperationException(message));

    private static void WriteMask(RegisterBanks banks, uint value) =>
        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, value, 0);

    [Fact]
    public void RetainingALiveContextReferenceDoesNotMakeIdentityAContentProof()
    {
        var banks = NewBanks();
        WriteMask(banks, 0xFu);
        var remembered = banks.Context;
        var rememberedMask = remembered.RenderTargetMask;

        WriteMask(banks, 3u);

        Assert.Same(remembered, banks.Context);
        Assert.NotEqual(rememberedMask, banks.Context.RenderTargetMask);
        Assert.Equal(3u, remembered.RenderTargetMask);
    }

    [Fact]
    public void RetainingASnapshotEstablishesTheProducerCopyObligation()
    {
        var banks = NewBanks();
        WriteMask(banks, 0xFu);
        var snapshot = banks.Snapshot();
        var remembered = snapshot.Context;

        WriteMask(banks, 3u);

        Assert.NotSame(remembered, banks.Context);
        Assert.Equal(0xFu, remembered.RenderTargetMask);
        Assert.Equal(3u, banks.Context.RenderTargetMask);
    }
}
