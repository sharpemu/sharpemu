// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Content invariants over the compile-time generated export registry
/// (SharpEmu.Generated.SysAbiExportRegistry), which is the runtime's sole registration
/// source. Replaces the parity test that pinned the registry to the retired reflection
/// scan while both existed; equality with the scan proved the swap, these pin what must
/// stay true now that only the registry remains.
/// </summary>
public sealed class SysAbiRegistryTests
{
    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    [InlineData(Generation.Gen4 | Generation.Gen5)]
    public void RegistryIsDuplicateFree(Generation generation)
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation);
        var manager = new ModuleManager();

        // RegisterExports skips NIDs it has already seen, so a shortfall here means the
        // generated table carries a duplicate the SHEM001 analyzer should have caught.
        Assert.Equal(exports.Count, manager.RegisterExports(exports));
    }

    [Fact]
    public void RegistryCoversTheFullExportSurface()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5);

        // 715 exports existed when the registry replaced the scan; shrinkage means the
        // generator silently dropped handlers.
        Assert.True(exports.Count >= 715, $"registry shrank to {exports.Count} exports");
    }

    [Fact]
    public void RegistryResolvesKnownExportWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport("Zxa0VhQVTsk", out var export));
        Assert.Equal("sceKernelWaitSema", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }
}
