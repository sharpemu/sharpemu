// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// These NIDs came back "unresolved" in the Quake (PPSA01880) import log right before its
// access violation. This asserts they now resolve to the Json handlers and dispatch cleanly,
// which is the plumbing the direct-call tests cannot cover.
[Collection("JsonObjectHeap")]
public sealed class JsonExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    {
        ("qBMjqyBn3OM", "_ZN3sce4Json5ValueC1Ev"),
        ("fSb2oQTNrgA", "_ZN3sce4Json5ValueC1ERKS1_"),
        ("5yHuiWXo2gg", "_ZN3sce4Json5Value3setEb"),
        ("QxVVYhP-mvg", "_ZN3sce4Json5Value3setEl"),
        ("SIe1ZmW7e7s", "_ZN3sce4Json5Value3setEm"),
        ("BSmWDIkV4w4", "_ZN3sce4Json5Value3setEd"),
        ("IKQimvG9Wqs", "_ZN3sce4Json5Value3setENS0_9ValueTypeE"),
        ("6l3Bv2gysNc", "_ZN3sce4Json5Value3setERKNS0_6StringE"),
        ("9KUZFjI1IxA", "_ZN3sce4Json6StringC1EPKc"),
        ("cG1VE2HMl6c", "_ZN3sce4Json6StringD1Ev"),
        ("+drDFyAS6u4", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
        ("GvGvswb0v34", "_ZN3sce4Json14InitParameter2C2Ev"),
        ("W72B9ylU2JA", "_ZN3sce4Json18InitParameterRtti216setAllocatorRttiEPNS0_14AllocParamRttiEPv"),
    };

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
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
    public void DispatchValueCopyConstructor_PreservesValueAfterSourceIsDestroyed()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        const ulong source = 0x1_0000_0000;
        const ulong destination = source + 0x100;
        var ctx = new CpuContext(new FakeCpuMemory(source, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = source;
        ctx[CpuRegister.Rsi] = unchecked((ulong)-42L);
        JsonValueExports.ValueIntegerConstructor(ctx);
        ctx[CpuRegister.Rdi] = destination;
        ctx[CpuRegister.Rsi] = source;
        Assert.True(manager.TryDispatch("fSb2oQTNrgA", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(destination, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rdi] = source;
        JsonExports.ValueDestructor(ctx);
        Assert.Equal(-42L, JsonObjectHeap.Values[destination].Integer);
        ctx[CpuRegister.Rdi] = destination;
        JsonExports.ValueGetType(ctx);
        Assert.Equal(2UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(destination + 0x10, out var integer));
        Assert.Equal(unchecked((ulong)-42L), integer);
    }

    [Fact]
    public void ValueCopyConstructor_CopiesParsedObjectInsteadOfDefaultConstructorShadow()
    {
        JsonObjectHeap.ResetForTests();
        const ulong source = 0x1_0000_0000;
        const ulong destination = source + 0x100;
        const ulong text = source + 0x200;
        var memory = new FakeCpuMemory(source, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = source;
        JsonExports.ValueConstructor(ctx);
        memory.WriteCString(text, "{\"a\":1,\"b\":[2,3]}");
        ctx[CpuRegister.Rsi] = text;
        ctx[CpuRegister.Rdx] = 17;
        Assert.Equal(0, JsonExports.ParserParseBuffer(ctx));
        ctx[CpuRegister.Rdi] = destination;
        ctx[CpuRegister.Rsi] = source;
        Assert.Equal(0, JsonExports.ValueCopyConstructor(ctx));
        ctx[CpuRegister.Rdi] = source;
        JsonExports.ValueDestructor(ctx);
        ctx[CpuRegister.Rdi] = destination;
        JsonExports.ValueGetType(ctx);
        Assert.Equal(7UL, ctx[CpuRegister.Rax]);
        JsonExports.ValueCount(ctx);
        Assert.Equal(2UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetGlobalNullAccessCallback_StoresHookAndReturnsOk()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000; // Initializer instance
        ctx[CpuRegister.Rsi] = 0x8_0012_3456; // guest callback
        ctx[CpuRegister.Rdx] = 0x1_0000_0800; // user context

        Assert.True(manager.TryDispatch("+drDFyAS6u4", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0x1_0000_0800UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
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

    [Fact]
    public void InitParameter2Aliases_InitializeAndWriteTheirDocumentedFields()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong parameterAddress = memoryBase + 0x100;
        const ulong allocatorAddress = 0x8_0012_3000;
        const ulong userDataAddress = memoryBase + 0x500;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(parameterAddress, Enumerable.Repeat((byte)0xA5, 0x28).ToArray()));

        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.True(manager.TryDispatch("GvGvswb0v34", ctx, out var constructorResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, constructorResult);

        var parameter = new byte[0x28];
        Assert.True(memory.TryRead(parameterAddress, parameter));
        Assert.All(parameter, value => Assert.Equal(0, value));

        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = allocatorAddress;
        ctx[CpuRegister.Rdx] = userDataAddress;
        Assert.True(manager.TryDispatch("W72B9ylU2JA", ctx, out var allocatorResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, allocatorResult);

        Assert.True(memory.TryRead(parameterAddress, parameter));
        Assert.Equal(allocatorAddress, BitConverter.ToUInt64(parameter, 0));
        Assert.Equal(userDataAddress, BitConverter.ToUInt64(parameter, 8));
        Assert.All(parameter[0x10..], value => Assert.Equal(0, value));
    }
}
