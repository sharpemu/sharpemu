// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// These NIDs came back "unresolved" in the Quake (PPSA01880) import log right before its
// access violation. This asserts they now resolve to the Json handlers and dispatch cleanly,
// which is the plumbing the direct-call tests cannot cover.
public sealed class JsonExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    {
        ("qBMjqyBn3OM", "_ZN3sce4Json5ValueC1Ev"),
        ("5yHuiWXo2gg", "_ZN3sce4Json5Value3setEb"),
        ("QxVVYhP-mvg", "_ZN3sce4Json5Value3setEl"),
        ("SIe1ZmW7e7s", "_ZN3sce4Json5Value3setEm"),
        ("BSmWDIkV4w4", "_ZN3sce4Json5Value3setEd"),
        ("IKQimvG9Wqs", "_ZN3sce4Json5Value3setENS0_9ValueTypeE"),
        ("6l3Bv2gysNc", "_ZN3sce4Json5Value3setERKNS0_6StringE"),
        ("9KUZFjI1IxA", "_ZN3sce4Json6StringC1EPKc"),
        ("cG1VE2HMl6c", "_ZN3sce4Json6StringD1Ev"),
    };

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(JsonValueExports).Assembly, Generation.Gen5);
        return manager;
    }

    [Fact]
    public void QuakeUnresolvedJsonNids_ResolveToJsonExports()
    {
        var manager = CreateRegisteredManager();

        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceJson", export.LibraryName);
        }
    }

    [Fact]
    public void DispatchValueConstructor_RunsHandlerAndReturnsThis()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000;

        Assert.True(manager.TryDispatch("qBMjqyBn3OM", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x1_0000_0000UL, ctx[CpuRegister.Rax]);
        Assert.Equal(JsonValueKind.Null, JsonObjectHeap.Values[0x1_0000_0000].Kind);
    }
}
