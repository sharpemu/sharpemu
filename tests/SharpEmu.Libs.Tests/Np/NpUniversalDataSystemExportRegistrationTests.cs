// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpUniversalDataSystemExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    [
        ("jZCqWFgMehE", "sceNpUniversalDataSystemAbortHandle"),
        ("5zBnau1uIEo", "sceNpUniversalDataSystemCreateContext"),
        ("p+GcLqwpL9M", "sceNpUniversalDataSystemCreateEvent"),
        ("Hm7qubT3b70", "sceNpUniversalDataSystemCreateEventPropertyArray"),
        ("s6W4Zl4Slgk", "sceNpUniversalDataSystemCreateEventPropertyObject"),
        ("hT0IAEvN+M0", "sceNpUniversalDataSystemCreateHandle"),
        ("wB7IWzGp2v0", "sceNpUniversalDataSystemDestroyContext"),
        ("wG+84pnNIuo", "sceNpUniversalDataSystemDestroyEvent"),
        ("W-0xwY0ZMjw", "sceNpUniversalDataSystemDestroyEventPropertyArray"),
        ("kKUH0Viib3c", "sceNpUniversalDataSystemDestroyEventPropertyObject"),
        ("AUIHb7jUX3I", "sceNpUniversalDataSystemDestroyHandle"),
        ("rdi9BAfDLq8", "sceNpUniversalDataSystemEventPropertyArraySetArray"),
        ("0+l4QSWCM4E", "sceNpUniversalDataSystemEventPropertyArraySetBool"),
        ("JmgwKm96Lq4", "sceNpUniversalDataSystemEventPropertyArraySetFloat32"),
        ("XY14n3jNIpE", "sceNpUniversalDataSystemEventPropertyArraySetObject"),
        ("4llLk7YJRTE", "sceNpUniversalDataSystemEventPropertyArraySetString"),
        ("Wxbg5x3pTXA", "sceNpUniversalDataSystemEventPropertyObjectSetArray"),
        ("Fidd8vWgyVE", "sceNpUniversalDataSystemEventPropertyObjectSetBool"),
        ("lbPlT4+QVcE", "sceNpUniversalDataSystemEventPropertyObjectSetFloat32"),
        ("4Fu8tHW+u-k", "sceNpUniversalDataSystemEventPropertyObjectSetFloat64"),
        ("YE4dbtbz6OE", "sceNpUniversalDataSystemEventPropertyObjectSetInt32"),
        ("56QLTqx911s", "sceNpUniversalDataSystemEventPropertyObjectSetInt64"),
        ("74ASEqxSnkM", "sceNpUniversalDataSystemEventPropertyObjectSetObject"),
        ("MfDb+4Nln64", "sceNpUniversalDataSystemEventPropertyObjectSetString"),
        ("AzD4irAcKE4", "sceNpUniversalDataSystemEventPropertyObjectSetUInt32"),
        ("xvsP5Yz6FmY", "sceNpUniversalDataSystemEventPropertyObjectSetUInt64"),
        ("su7jW3VDDb4", "sceNpUniversalDataSystemGetMemoryStat"),
        ("sjaobBgqeB4", "sceNpUniversalDataSystemInitialize"),
        ("CzkKf7ahIyU", "sceNpUniversalDataSystemPostEvent"),
        ("tpFJ8LIKvPw", "sceNpUniversalDataSystemRegisterContext"),
        ("47UAEuQl+iI", "sceNpUniversalDataSystemTerminate"),
    ];

    [Fact]
    public void AllLocallyObservedExports_AreRegisteredWithTheUdsLibrary()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceNpUniversalDataSystem", export.LibraryName);
        }
    }
}
