// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class UnsupportedInstructionDescriptionTests
{
    private static string Describe(byte[] code)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
        decoder.Decode(out var instruction);
        return DirectExecutionBackend.DescribeUnsupportedInstruction(in instruction);
    }

    [Fact]
    public void NamesSse4aForTheInstructionDeadCellsFaultsOn()
    {
        // Verbatim bytes at the faulting RIP of PPSA15552 under Rosetta 2 (#328):
        // extrq xmm1, 0x28, 0. SSE4a is AMD-only, so no Apple/Intel host implements it.
        var description = Describe([0x66, 0x0F, 0x78, 0xC1, 0x28, 0x00]);

        Assert.Equal(
            "Unsupported instruction: Extrq (requires SSE4A; host does not implement it)",
            description);
    }

    [Fact]
    public void NamesAvx2ForTheInstructionFollowingIt()
    {
        // vpblendd xmm0, xmm0, xmm1, 2 -- six bytes past the extrq above.
        var description = Describe([0xC4, 0xE3, 0x79, 0x02, 0xC1, 0x02]);

        Assert.Contains("Vpblendd", description);
        Assert.Contains("AVX2", description);
    }

    [Fact]
    public void NamesTheExtensionForAnInstructionTheBmiFallbackAlreadyEmulates()
    {
        // pdep rax, rbx, rcx -- recovered by TryRecoverIllegalInstruction, so it
        // never reaches this path in practice; proves the naming is not SSE-specific.
        var description = Describe([0xC4, 0xE2, 0xE3, 0xF5, 0xC1]);

        Assert.Contains("Pdep", description);
        Assert.Contains("BMI2", description);
    }

}
