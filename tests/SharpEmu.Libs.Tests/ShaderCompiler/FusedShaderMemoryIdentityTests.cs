// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class FusedShaderMemoryIdentityTests
{
    [Fact]
    public void WrappersShareRegistrationsWithoutSharingUnrelatedMemory()
    {
        var memory = new FakeCpuMemory(0x1000, 0x1000);
        var wrapped = new CpuContext(new MemoryWrapper(new MemoryWrapper(memory)), Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(wrapped, 0x1000, 0x1400, 0x1100, 0x1500);

        foreach (var view in new ICpuMemory[] { memory, new MemoryWrapper(memory) })
        {
            Assert.True(Gen5ShaderTranslator.TryGetFusedProgramParts(
                new CpuContext(view, Generation.Gen5), 0x1000, out var continuation, out var header));
            Assert.Equal(0x1100ul, continuation);
            Assert.Equal(0x1500ul, header);
        }

        var unrelated = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        Assert.False(Gen5ShaderTranslator.TryGetFusedProgramParts(unrelated, 0x1000, out _, out _));
    }

    private sealed class MemoryWrapper(ICpuMemory memory) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner => memory;
        public bool TryRead(ulong address, Span<byte> destination) => memory.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => memory.TryWrite(address, source);
    }
}
