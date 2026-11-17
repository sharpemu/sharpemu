// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Wave64LoweringChoiceTests
{
    [Theory]
    // unset: every workgroup is paired, as before the one-wave split existed.
    [InlineData(null, 32, true)]
    [InlineData(null, 64, true)]
    [InlineData(null, 1024, true)]
    // small: a one-wave (or partial-wave) workgroup runs its halves as two host subgroups, a larger one is paired.
    [InlineData("small", 32, false)]
    [InlineData("small", 64, false)]
    [InlineData("small", 65, true)]
    [InlineData("small", 1024, true)]
    [InlineData("1", 64, true)]
    [InlineData("1", 1024, true)]
    [InlineData("0", 64, false)]
    [InlineData("0", 1024, false)]
    public void PairedLanesFollowTheModeAndWorkgroupSize(string? mode, ulong threads, bool paired) =>
        Assert.Equal(paired, Gen5SpirvTranslator.PairsWave64Lanes(mode, threads));
}
