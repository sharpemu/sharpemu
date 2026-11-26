// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// AGC exports that only touch guest memory through their pointer arguments take the cheaper import dispatch tiers.
public sealed class ImportLeafTierTests
{
    [Theory]
    [InlineData("sceAgcSetCxRegIndirectPatchAddRegisters", "d-6uF9sZDIU")]
    [InlineData("sceAgcSetShRegIndirectPatchAddRegisters", "z2duB-hHQSM")]
    [InlineData("sceAgcSetUcRegIndirectPatchAddRegisters", "vRoArM9zaIk")]
    [InlineData("sceAgcCreatePrimState", "D9sr1xGUriE")]
    public void RegisterPatchesAndPrimitiveStateTakeTheTrivialLeafTier(string exportName, string nid)
    {
        Assert.Equal(nid, FindAgcNid(exportName));
        Assert.True(Classify("IsTrivialLeafImport", null, nid));
    }

    [Theory]
    [InlineData("sceAgcCbSetShRegisterRangeDirect", "n2fD4A+pb+g")]
    [InlineData("sceAgcDcbDrawIndexIndirect", "t1vNu082-jM")]
    public void PacketWritersTakeTheLeafTier(string exportName, string nid)
    {
        Assert.Equal(nid, FindAgcNid(exportName));
        // Both can call the guest's buffer-full callback, so they keep the leaf tier's import call frame.
        Assert.False(Classify("IsTrivialLeafImport", null, nid));
        Assert.True(Classify("IsLeafImport", RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend)), nid));
    }

    private static string FindAgcNid(string exportName) =>
        Assert.Single(typeof(AgcExports).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetCustomAttributes<SysAbiExportAttribute>()),
            attribute => attribute.ExportName == exportName).Nid;

    private static bool Classify(string methodName, object? backend, string nid)
    {
        var method = typeof(DirectExecutionBackend).GetMethod(methodName,
            BindingFlags.NonPublic | (backend is null ? BindingFlags.Static : BindingFlags.Instance));
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(backend, [nid]));
    }
}
