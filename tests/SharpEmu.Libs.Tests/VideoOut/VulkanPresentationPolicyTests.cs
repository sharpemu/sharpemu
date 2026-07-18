// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPresentationPolicyTests
{
    [Theory]
    [InlineData(false, GuestDrawKind.None, false, 0, false, true)]
    [InlineData(true, GuestDrawKind.None, false, 0, false, false)]
    [InlineData(false, GuestDrawKind.FullscreenBarycentric, false, 0, false, false)]
    [InlineData(false, GuestDrawKind.None, true, 0, false, false)]
    [InlineData(false, GuestDrawKind.None, false, 0x105E0000, false, false)]
    [InlineData(false, GuestDrawKind.None, false, 0, true, false)]
    public void EmptyPresentationRequiresNoHostOrGuestContent(
        bool hasPixels,
        GuestDrawKind drawKind,
        bool hasTranslatedDraw,
        ulong guestImageAddress,
        bool hasGuestImageCapture,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsPresentationEmpty(
                hasPixels,
                drawKind,
                hasTranslatedDraw,
                guestImageAddress,
                hasGuestImageCapture));
    }
}
