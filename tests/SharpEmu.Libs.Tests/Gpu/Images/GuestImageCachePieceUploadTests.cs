// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class GuestImageCachePieceUploadTests
{
    [Fact]
    public void PieceHashesRejectStencilAssociationBeforeColorLayoutPlanning()
    {
        const ulong largeImage = 16UL << 20;

        Assert.False(GuestImageCache.CanUsePieceHashes(
            ImageRole.Texture,
            isStencilAssociation: true,
            isVolume: false,
            isDepth: false,
            largeImage));

        Assert.True(GuestImageCache.CanUsePieceHashes(
            ImageRole.Texture,
            isStencilAssociation: false,
            isVolume: false,
            isDepth: false,
            largeImage));
    }
}
