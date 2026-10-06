// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// #870: the translator ran every 64-thread compute workgroup through the software
// wave64-over-two-wave32 bridge, even on hardware whose native subgroup size is
// already 64 - an RDNA 9070 XT reports subgroupSize = 64 by default, so the
// bridge was unrequested overhead sitting between the emulated wave and a host
// wave of the same width.
//
// The predicate decides whether that bridge is needed at all, so pin it directly
// rather than trying to diff emitted SPIR-V.
public sealed class RequiresSoftwareWave64Tests
{
    private const bool Compute = true;
    private const bool NotCompute = false;

    [Fact]
    public void NativeWave64Compute_DoesNotNeedTheBridge()
    {
        // The case this fixes: guest wave64, host subgroup size 64.
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: 64));
    }

    [Fact]
    public void Wave64ComputeOnA32LaneHost_StillNeedsTheBridge()
    {
        Assert.True(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: 32));
    }

    [Fact]
    public void UnreportedHostSubgroupSize_KeepsThePreviousConservativeBehaviour()
    {
        // 0 means the host did not report. This must behave exactly as before the
        // change, so no hardware silently loses the bridge.
        Assert.True(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: 0));
    }

    [Fact]
    public void Wave32Compute_NeverNeedsTheBridge()
    {
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 32, totalWorkgroupSize: 32, nativeComputeSubgroupSize: 0));
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 32, totalWorkgroupSize: 64, nativeComputeSubgroupSize: 0));
    }

    [Fact]
    public void NonComputeStages_NeverNeedTheBridge()
    {
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            NotCompute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: 0));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(32UL)]
    [InlineData(96UL)]
    [InlineData(128UL)]
    [InlineData(192UL)]
    public void WorkgroupsThatAreNotExactlyOneWave_NeverNeedTheBridge(ulong totalWorkgroupSize)
    {
        // The bridge only ever applied to a 64-thread workgroup, so these were
        // already left alone and must stay that way.
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: totalWorkgroupSize, nativeComputeSubgroupSize: 0));
    }

    [Theory]
    [InlineData(64u)]
    [InlineData(128u)]
    [InlineData(256u)]
    public void AHostSubgroupAtLeastAsWideAsTheWave_CoversIt(uint nativeSubgroupSize)
    {
        // "Natively covers" the requested wave width: a subgroup wider than 64 still
        // contains the emulated wave, so no bridge is needed. This matches the
        // existing IShaderPipelineHost.ComputeWave64Supported (>= 64), which already
        // drives the compute stage input resolver the same way.
        Assert.False(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: nativeSubgroupSize));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(32u)]
    public void AHostNarrowerThanTheWave_CannotRunItNatively(uint nativeSubgroupSize)
    {
        Assert.True(ShaderCompileRequest.RequiresSoftwareWave64(
            Compute, waveLaneCount: 64, totalWorkgroupSize: 64, nativeComputeSubgroupSize: nativeSubgroupSize));
    }
}
