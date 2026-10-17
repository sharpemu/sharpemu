// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelInputMappingTests
{
    [Fact]
    public void ResolveLocations_PreservesUniqueMappings()
    {
        uint[] controls = [5, 7, 9];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [5u, 7u, 9u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RelocatesDuplicateMappings()
    {
        uint[] controls = [0, 0, 0];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [0u, 1u, 2u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_SkipsOccupiedFallbackLocations()
    {
        uint[] controls = [1, 1, 2];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [1u, 2u, 3u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_UsesIdentityBeyondProgrammedControls()
    {
        uint[] controls = [0x407];
        uint[] activeInputs = [0, 2, 5];

        Assert.Equal(
            [7u, 2u, 5u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RejectsFallbackPastLastLocation()
    {
        var controls = new uint[32];
        controls[0] = 31;
        controls[31] = 31;
        uint[] activeInputs = [0, 31];

        Assert.Throws<InvalidOperationException>(() =>
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }
}
