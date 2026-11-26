// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class DriverShaderCacheFolderTests
{
    [Fact]
    public void TheGameFolderIsGivenByDefaultUnlessSwitchedOffOrAPathIsSet()
    {
        Assert.Equal("cache/seed.bin.driver", DriverShaderCacheFolder.Resolve(null, null, "cache/seed.bin"));
        Assert.Equal("cache/seed.bin.driver", DriverShaderCacheFolder.Resolve("1", "", "cache/seed.bin"));
        Assert.Null(DriverShaderCacheFolder.Resolve("0", null, "cache/seed.bin"));
        // A path the user or a runner set is kept.
        Assert.Null(DriverShaderCacheFolder.Resolve(null, "elsewhere", "cache/seed.bin"));
    }

    [Fact]
    public void TheSwitchDoesNotChangeTranslationKeys() =>
        Assert.True(TranslationDiskCache.IgnoredVariable("SHARPEMU_DRIVER_CACHE_FOLDER"));
}
