// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class RuntimeSymbolAliasTests
{
    [Fact]
    public void ScriptingGetMemResolvesEncodedMemalignSymbol()
    {
        const ulong memalignAddress = 0x8000_2000;
        var backend = CreateBackend(new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["Ujf3KzMvRmI"] = memalignAddress,
        });

        var (resolved, address) = ResolveAlias(backend, "scriptingGetMem");

        Assert.True(resolved);
        Assert.Equal(memalignAddress, address);
    }

    [Fact]
    public void ScriptingGetMemDoesNotResolveMallocSymbol()
    {
        var backend = CreateBackend(new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["gQX+4GDQjpM"] = 0x8000_3000,
        });

        var (resolved, address) = ResolveAlias(backend, "scriptingGetMem");

        Assert.False(resolved);
        Assert.Equal(0UL, address);
    }

    private static DirectExecutionBackend CreateBackend(Dictionary<string, ulong> runtimeSymbols)
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        typeof(DirectExecutionBackend)
            .GetField("_runtimeSymbolsByName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(backend, runtimeSymbols);
        return backend;
    }

    private static (bool Resolved, ulong Address) ResolveAlias(
        DirectExecutionBackend backend,
        string symbolName)
    {
        var arguments = new object?[] { symbolName, 0UL };
        var result = Assert.IsType<bool>(typeof(DirectExecutionBackend)
            .GetMethod("TryResolveRuntimeSymbolAlias", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(backend, arguments));
        return (result, Assert.IsType<ulong>(arguments[1]));
    }
}
