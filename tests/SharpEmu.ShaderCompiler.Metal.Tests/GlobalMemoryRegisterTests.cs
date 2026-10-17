// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class GlobalMemoryRegisterTests
{
    [Theory]
    [InlineData(12u, 1)]
    [InlineData(13u, 2)]
    [InlineData(14u, 4)]
    [InlineData(15u, 3)]
    public void Loads_WriteTheDestinationRegisters(uint opcode, int componentCount)
    {
        var source = Compile(opcode);
        for (var component = 0; component < componentCount; component++)
            Assert.Contains($"v[{8 + component}] = sharpemu_load_device_dword", source);
        Assert.DoesNotContain("v[3] =", source);
    }

    [Theory]
    [InlineData(28u, 1)]
    [InlineData(29u, 2)]
    [InlineData(30u, 4)]
    [InlineData(31u, 3)]
    public void Stores_ReadTheSourceRegisters(uint opcode, int componentCount)
    {
        var source = Compile(opcode);
        for (var component = 0; component < componentCount; component++)
            Assert.Matches($@"sharpemu_store_device_dword\([^\r\n]*, v\[{3 + component}\]\);", source);
        Assert.DoesNotContain(", v[8],", source);
    }

    [Theory]
    [InlineData(50u, false, "add")]
    [InlineData(50u, true, "add")]
    [InlineData(56u, false, "max")]
    [InlineData(56u, true, "max")]
    public void Atomics_ReadTheSourceAndOnlyWriteRequestedResults(uint opcode, bool returnsValue, string operation)
    {
        var source = Compile(opcode, returnsValue);
        Assert.Contains($"atomic_fetch_{operation}_explicit", source);
        Assert.Contains(", v[3], memory_order_relaxed)", source);
        Assert.Equal(returnsValue, source.Contains("v[8] =", StringComparison.Ordinal));
        Assert.DoesNotContain("v[3] =", source);
    }

    [Theory]
    [InlineData(32u, false)]
    [InlineData(33u, true)]
    [InlineData(34u, false)]
    [InlineData(35u, true)]
    [InlineData(36u, false)]
    [InlineData(37u, true)]
    public void PartialLoads_PreserveTheOtherHalfOfTheDestination(uint opcode, bool highHalf)
    {
        var source = Compile(opcode);
        Assert.Contains(highHalf ? "v[8] & 0x0000FFFFu" : "v[8] & 0xFFFF0000u", source);
        Assert.DoesNotContain("v[3]", source);
    }

    private static string Compile(uint opcode, bool returnsValue = false)
    {
        var word = 0xDC00_8000u | (opcode << 18) | (returnsValue ? 1u << 16 : 0);
        var fixture = new Gen5ComputeFixture("global-registers", [word, 0x0810_0309, 0xBF81_0000], 16, 64);
        return Gen5ComputeFixtures.CompileRequestOrThrow(fixture).Source;
    }
}
