// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportStubEntryTests
{
    private const string MissingNid = "s9e3+YpRnzw";

    [Fact]
    public void PrecomputesTheMissingExportErrorMessage()
    {
        var entryType = typeof(DirectExecutionBackend).GetNestedType(
            "ImportStubEntry",
            BindingFlags.NonPublic);
        Assert.NotNull(entryType);

        var missingExportError = entryType.GetProperty(
            "MissingHleExportError",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(missingExportError);

        var constructor = Assert.Single(entryType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        var entry = constructor.Invoke([0UL, MissingNid, null, false, false, false, false, 0UL]);

        var first = (string?)missingExportError.GetValue(entry);
        var second = (string?)missingExportError.GetValue(entry);

        Assert.Equal($"Missing HLE export for NID: {MissingNid}", first);
        Assert.Same(first, second);
    }

    [Fact]
    public void DispatchImport_UsesTheCachedMissingExportError()
    {
        var dispatchImport = typeof(DirectExecutionBackend).GetMethod(
            "DispatchImport",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dispatchImport);

        Assert.Contains("get_MissingHleExportError", ResolveCallees(dispatchImport));
    }

    private static HashSet<string> ResolveCallees(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);

        var module = method.Module;
        var generic = method.DeclaringType?.GetGenericArguments();
        var callees = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var callee = module.ResolveMethod(token, generic, null);
                if (callee?.Name is { } name)
                {
                    callees.Add(name);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return callees;
    }
}
