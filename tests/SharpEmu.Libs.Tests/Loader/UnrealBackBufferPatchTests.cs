// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class UnrealBackBufferPatchTests
{
    private const ulong CodeBase = 0x800000000UL + 0x174d7d4;
    private const ulong NameAddress = 0x800000000UL + 0x06b1def8;

    // The registration as shipped: lea rsi,[rip+name]; lea rcx,[rip+help]; mov edx,0xF00; mov r8d,4; call [rax+8].
    private static byte[] Registration(int nameDisplacement, uint defaultValue)
    {
        var code = new byte[]
        {
            0x48, 0x8D, 0x35, 0, 0, 0, 0,
            0x48, 0x8D, 0x0D, 0x11, 0x22, 0x33, 0x44,
            0xBA, 0, 0, 0, 0,
            0x41, 0xB8, 0x04, 0x00, 0x00, 0x00,
            0xFF, 0x50, 0x08,
        };
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(3), nameDisplacement);
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(15), defaultValue);
        return code;
    }

    [Fact]
    public void LocatesTheDefaultImmediateBehindTheNameLoad()
    {
        var displacement = (int)(NameAddress - (CodeBase + 7));
        var prefix = new byte[] { 0x90, 0x90, 0x90 };
        var code = prefix.Concat(Registration(displacement, 3840)).ToArray();

        var site = UnrealBackBufferPatch.TryLocateDefault(code, CodeBase - (ulong)prefix.Length, NameAddress);

        Assert.NotNull(site);
        Assert.Equal(NameAddress, site.Value.NameAddress);
        Assert.Equal(CodeBase + 14 + 1, site.Value.ImmediateAddress);
        Assert.Equal(3840u, site.Value.CurrentValue);
    }

    [Fact]
    public void IgnoresNameLoadsOfOtherVariables()
    {
        var displacement = (int)(NameAddress - (CodeBase + 7));
        var code = Registration(displacement + 0x40, 3840);

        Assert.Null(UnrealBackBufferPatch.TryLocateDefault(code, CodeBase, NameAddress));
    }

    [Fact]
    public void StopsAtControlFlowBeforeTheImmediate()
    {
        var displacement = (int)(NameAddress - (CodeBase + 7));
        var code = Registration(displacement, 3840);
        // Turn the help-string lea into a ret so the name load is not followed by a registration.
        code[7] = 0xC3;

        Assert.Null(UnrealBackBufferPatch.TryLocateDefault(code, CodeBase, NameAddress));
    }
}
