// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpCommerceExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    [
        ("0aR2aWmQal4", "sceNpCommerceDialogInitialize"),
        ("m-I92Ab50W8", "sceNpCommerceDialogTerminate"),
        ("DfSCDRA3EjY", "sceNpCommerceDialogOpen"),
        ("IXmfUaze9So", "sceNpCommerceDialogOpen2"),
        ("LR5cwFMMCVE", "sceNpCommerceDialogUpdateStatus"),
        ("r42bWcQbtZY", "sceNpCommerceDialogGetResult"),
    ];

    [Fact]
    public void AllLocallyObservedExports_AreRegisteredWithTheCommerceLibrary()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceNpCommerce", export.LibraryName);
        }
    }
}
