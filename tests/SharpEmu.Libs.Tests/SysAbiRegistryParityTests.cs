// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Pins the compile-time generated export registry (SharpEmu.Generated.
/// SysAbiExportRegistry) to the reflection scan it replaced: same NIDs, and for every
/// NID the same name, library, generation target, and handler method. The runtime
/// registers through the generated path; this test is what keeps the retired scan
/// honest as the arbiter of ground truth.
/// </summary>
public sealed class SysAbiRegistryParityTests
{
    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    [InlineData(Generation.Gen4 | Generation.Gen5)]
    public void GeneratedRegistryMatchesReflectionScan(Generation generation)
    {
        var scanned = new ModuleManager();
        scanned.RegisterFromAssembly(typeof(CxaGuardExports).Assembly, generation);

        var generated = new ModuleManager();
        generated.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

        var scannedExports = scanned.ExportsForTesting;
        var generatedExports = generated.ExportsForTesting;

        var missing = new List<string>();
        foreach (var pair in scannedExports)
        {
            if (!generatedExports.ContainsKey(pair.Key))
            {
                missing.Add($"{pair.Key} ({pair.Value.Name})");
            }
        }

        var extra = new List<string>();
        foreach (var pair in generatedExports)
        {
            if (!scannedExports.ContainsKey(pair.Key))
            {
                extra.Add($"{pair.Key} ({pair.Value.Name})");
            }
        }

        Assert.True(missing.Count == 0, "generated registry is missing: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, "generated registry has extras: " + string.Join(", ", extra));

        foreach (var pair in scannedExports)
        {
            var expected = pair.Value;
            var actual = generatedExports[pair.Key];
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.LibraryName, actual.LibraryName);
            Assert.Equal(expected.Target, actual.Target);
            Assert.Same(expected.Function.Method, actual.Function.Method);
        }
    }
}
