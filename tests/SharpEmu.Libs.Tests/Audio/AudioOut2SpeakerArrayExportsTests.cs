// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2SpeakerArrayExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong HandleAddress = MemoryBase + 0x100;
    private const ulong FaultingAddress = 0xDEAD_0000_0000;

    [Fact]
    public void SpeakerArrayExportsAreRegisteredForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("G1YOKDJYX2Y", out var getSize));
        Assert.Equal("sceAudioOut2GetSpeakerArrayMemorySize", getSize.Name);
        Assert.True(manager.TryGetExport("+k91hoTuoA8", out var create));
        Assert.Equal("sceAudioOut2SpeakerArrayCreate", create.Name);
    }

    [Fact]
    public void GetSpeakerArrayMemorySizeReturnsRequiredSize()
    {
        var context = CreateContext();

        Assert.Equal(0, AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(context));
        Assert.Equal(0x10000UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void SpeakerArrayCreateWritesNonzeroHandle()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = HandleAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2SpeakerArrayCreate(context));
        Assert.True(context.TryReadUInt64(HandleAddress, out var handle));
        Assert.NotEqual(0UL, handle);
    }

    [Theory]
    [InlineData(0UL, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)]
    [InlineData(FaultingAddress, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void SpeakerArrayCreateRejectsInvalidOutput(
        ulong outputAddress,
        OrbisGen2Result expected)
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = outputAddress;

        Assert.Equal((int)expected, AudioOut2Exports.AudioOut2SpeakerArrayCreate(context));
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
}
