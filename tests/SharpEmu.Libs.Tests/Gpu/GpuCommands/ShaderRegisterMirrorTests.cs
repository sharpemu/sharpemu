// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class ShaderRegisterMirrorTests
{
    [Fact]
    public void WrittenRegisters_ReadBackInOffsetOrder()
    {
        var mirror = new ShaderRegisterMirror();
        mirror[0x2FF] = 3;
        mirror.SetRange(0x40, new uint[] { 1, 2 });
        mirror[0x40] = 5;

        Assert.Equal(3, mirror.Count);
        Assert.Equal(new[] { new KeyValuePair<uint, uint>(0x40, 5), new(0x41, 2), new(0x2FF, 3) }, mirror);
        Assert.True(mirror.ContainsKey(0x41));
        Assert.False(mirror.TryGetValue(0x42, out _));
        Assert.Throws<KeyNotFoundException>(() => mirror[0x42]);
    }

    [Fact]
    public void Clear_ForgetsEveryRegisterIncludingOnesOutsideTheBank()
    {
        var mirror = new ShaderRegisterMirror();
        mirror[0x10] = 1;
        mirror[0x1_0000] = 2;
        Assert.Equal(2, mirror.Count);
        Assert.Equal(2u, mirror[0x1_0000]);

        mirror.Clear();

        Assert.Empty(mirror);
        Assert.False(mirror.ContainsKey(0x10));
        Assert.False(mirror.ContainsKey(0x1_0000));
        mirror[0x10] = 7;
        Assert.Equal(new[] { new KeyValuePair<uint, uint>(0x10, 7) }, mirror);
    }
}
