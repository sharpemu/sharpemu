// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class ShaderTranslationProfileTests
{
    [Fact]
    public void Scope_RecordsFailureAndStopsOnDispose()
    {
        var profile = typeof(ShaderTranslationProfile);
        var scopeType = profile.GetNestedType("Scope", BindingFlags.NonPublic)!;
        var partType = profile.GetNestedType("Part", BindingFlags.NonPublic)!;
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var part = Enum.Parse(partType, "Validation");
        var scope = scopeType.GetConstructor(members, null, [partType], null)!.Invoke([part]);
        var enabled = (bool)profile.GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var start = scopeType.GetField("_start", members)!;
        Assert.Equal(enabled, (long)start.GetValue(scope)! != 0);
        try
        {
            scopeType.GetMethod("Switch", members)!.Invoke(scope, [Enum.Parse(partType, "Blocks")]);
            throw new InvalidOperationException("Test failure.");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            scopeType.GetMethod("Dispose", members)!.Invoke(scope, null);
        }
        Assert.Equal(0L, start.GetValue(scope));
        scopeType.GetMethod("Switch", members)!.Invoke(scope, [Enum.Parse(partType, "Emission")]);
        scopeType.GetMethod("Dispose", members)!.Invoke(scope, null);
        Assert.Equal(0L, start.GetValue(scope));
        Assert.Equal(enabled ? Enum.Parse(partType, "Blocks") : part, scopeType.GetField("_part", members)!.GetValue(scope));
    }
}
