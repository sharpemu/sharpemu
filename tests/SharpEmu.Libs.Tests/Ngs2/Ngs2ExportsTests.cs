// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

[CollectionDefinition(Ngs2StateCollection.Name, DisableParallelization = true)]
public sealed class Ngs2StateCollection
{
    public const string Name = "Ngs2State";
}

[Collection(Ngs2StateCollection.Name)]
public sealed class Ngs2ExportsTests : IDisposable
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);

    private const ulong MemoryBase = 0x1_0000_0000UL;
    private const ulong OutAddress = MemoryBase + 0x100;

    private const ulong SystemHandle = 0x0001_0000UL;
    private const ulong FirstRackHandle = 0x0002_0000UL;
    private const ulong SecondRackHandle = 0x0002_0100UL;
    private const ulong VoiceHandle = 0x0003_0000UL;
    private const uint VoiceIndex = 0;
    private const uint RackId = 1;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public Ngs2ExportsTests()
    {
        Ngs2Exports.ClearStateForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => Ngs2Exports.ClearStateForTests();

    [Fact]
    public void RackGetVoiceHandle_NullRackHandleWithSingleRack_FallsBackAndSucceeds()
    {
        // Dead Cells (#259): game passes rdi=0 instead of the handle from
        // sceNgs2RackCreate. With exactly one rack in flight we fall back to it.
        Ngs2Exports.AddRackForTests(FirstRackHandle, SystemHandle, RackId);
        Ngs2Exports.AddVoiceForTests(VoiceHandle, FirstRackHandle, VoiceIndex);

        var first = CallRackGetVoiceHandle(rackHandle: 0, voiceIndex: VoiceIndex, outAddress: OutAddress);
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var firstVoiceHandle));

        var second = CallRackGetVoiceHandle(rackHandle: 0, voiceIndex: VoiceIndex, outAddress: OutAddress);
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var secondVoiceHandle));

        Assert.Equal(0, first);
        Assert.NotEqual(0UL, firstVoiceHandle);
        Assert.Equal(0, second);
        Assert.Equal(firstVoiceHandle, secondVoiceHandle);
    }

    [Fact]
    public void RackGetVoiceHandle_NullRackHandleWithNoRacks_ReturnsInvalidRackHandle()
    {
        var result = CallRackGetVoiceHandle(rackHandle: 0, voiceIndex: VoiceIndex, outAddress: OutAddress);

        Assert.Equal(OrbisNgs2ErrorInvalidRackHandle, result);
    }

    [Fact]
    public void RackGetVoiceHandle_NullRackHandleWithMultipleRacks_ReturnsInvalidRackHandle()
    {
        Ngs2Exports.AddRackForTests(FirstRackHandle, SystemHandle, RackId);
        Ngs2Exports.AddRackForTests(SecondRackHandle, SystemHandle, RackId);

        var result = CallRackGetVoiceHandle(rackHandle: 0, voiceIndex: VoiceIndex, outAddress: OutAddress);

        Assert.Equal(OrbisNgs2ErrorInvalidRackHandle, result);
    }

    [Fact]
    public void RackGetVoiceHandle_InvalidRackHandle_ReturnsInvalidRackHandle()
    {
        Ngs2Exports.AddRackForTests(FirstRackHandle, SystemHandle, RackId);

        var result = CallRackGetVoiceHandle(rackHandle: 0xDEADBEEFUL, voiceIndex: VoiceIndex, outAddress: OutAddress);

        Assert.Equal(OrbisNgs2ErrorInvalidRackHandle, result);
        Assert.Equal(unchecked((ulong)OrbisNgs2ErrorInvalidRackHandle), _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void RackGetVoiceHandle_ValidRackHandle_ReturnsHandle()
    {
        Ngs2Exports.AddRackForTests(FirstRackHandle, SystemHandle, RackId);
        Ngs2Exports.AddVoiceForTests(VoiceHandle, FirstRackHandle, VoiceIndex);

        var result = CallRackGetVoiceHandle(rackHandle: FirstRackHandle, voiceIndex: VoiceIndex, outAddress: OutAddress);

        Assert.Equal(0, result);
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var voiceHandle));
        Assert.Equal(VoiceHandle, voiceHandle);
    }

    [Fact]
    public void RackGetVoiceHandle_NullOutAddress_ReturnsInvalidOutAddress()
    {
        // No voice is registered for the requested index, so the existing-voice
        // fast path is skipped and the explicit null out-address check triggers.
        Ngs2Exports.AddRackForTests(FirstRackHandle, SystemHandle, RackId);

        var result = CallRackGetVoiceHandle(rackHandle: FirstRackHandle, voiceIndex: VoiceIndex, outAddress: 0);

        Assert.Equal(OrbisNgs2ErrorInvalidOutAddress, result);
    }

    private int CallRackGetVoiceHandle(ulong rackHandle, uint voiceIndex, ulong outAddress)
    {
        _ctx[CpuRegister.Rdi] = rackHandle;
        _ctx[CpuRegister.Rsi] = voiceIndex;
        _ctx[CpuRegister.Rdx] = outAddress;
        return Ngs2Exports.Ngs2RackGetVoiceHandle(_ctx);
    }
}