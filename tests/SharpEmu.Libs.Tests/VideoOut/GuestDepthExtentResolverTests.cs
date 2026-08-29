// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestDepthExtentResolverTests
{
    [Fact]
    public void MismatchedSmallDepthTargetIsNotUsable()
    {
        var depth = new GuestDepthTarget(
            ReadAddress: 0x10000,
            WriteAddress: 0x10000,
            Width: 8,
            Height: 1,
            GuestFormat: 3,
            SwizzleMode: 0,
            ClearDepth: 1f,
            ReadOnly: true);

        var resolution = GuestDepthExtentResolver.Resolve(
            depth,
            colorWidth: 1920,
            colorHeight: 1080,
            textures: []);

        Assert.Equal(GuestDepthExtentResolutionKind.Mismatch, resolution.Kind);
        Assert.False(resolution.IsUsable);
    }
}
