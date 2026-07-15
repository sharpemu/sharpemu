// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Guards the build-time-generated aerolib.bin embedding: the catalog must load from
/// the assembly and resolve both directions, or the loader's import naming, the
/// not-implemented diagnostics, and runtime dlsym all silently degrade.
/// </summary>
public sealed class AerolibCatalogTests
{
    [Fact]
    public void EmbeddedCatalogResolvesKnownSymbolBothWays()
    {
        Assert.True(Aerolib.Instance.TryGetByExportName("sceKernelWaitSema", out var byName));
        Assert.Equal("Zxa0VhQVTsk", byName.Nid);

        Assert.True(Aerolib.Instance.TryGetByNid("Zxa0VhQVTsk", out var byNid));
        Assert.Equal("sceKernelWaitSema", byNid.ExportName);
    }
}
