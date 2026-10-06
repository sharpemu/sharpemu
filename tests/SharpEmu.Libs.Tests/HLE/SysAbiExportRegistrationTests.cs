// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Hle;

// #388: "a green build and contributor self-verification are not enough on their
// own". Several regressions here were exports that built and ran but could never
// be dispatched: a mistyped NID that no longer matches the aerolib catalog, or a
// NID bound to two handlers so one silently wins.
//
// These are the checks that can be made without a title or a GPU, so they belong
// in CI rather than in a reviewer's head. Deliberately not a snapshot of the
// export surface: adding an export must stay frictionless.
//
// Scoped to exports that declare a NID. Some exports deliberately omit it and are
// dispatched by ExportName + LibraryName instead (libSceHttp2, libSceNpAuth and
// others), so a blank NID is not itself a defect.
public sealed class SysAbiExportRegistrationTests
{
    // The exports live in SharpEmu.Libs, not in SharpEmu.HLE where
    // SysAbiExportAttribute itself is declared. Anchoring on the wrong assembly
    // makes every reflection-based check here vacuously pass, which is what
    // EveryExportIsReachableFromSomeLoadedAssembly guards against.
    //
    // No per-generation filtering here: a method can carry several SysAbiExport
    // attributes (one per generation), so a singular lookup throws.
    private static IReadOnlyList<MethodInfo> AllExports() =>
        typeof(KernelMemoryCompatExports).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetCustomAttributes<SysAbiExportAttribute>().Any())
            .ToArray();

    private static IReadOnlyList<SysAbiExportAttribute> Gen5NidExports() =>
        AllExports()
            .SelectMany(method => method.GetCustomAttributes<SysAbiExportAttribute>())
            .Where(attribute => attribute.Target.HasFlag(Generation.Gen5))
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Nid))
            .ToArray();

    [Fact]
    public void EveryExportHasTheDispatchableSignature()
    {
        // The import dispatcher invokes these as static methods whose first
        // parameter is the CpuContext and whose return value is the orbis result.
        // A method failing that shape registers, builds, passes review, and then
        // cannot be invoked at runtime.
        //
        // Arity past the first parameter is deliberately not constrained: a few
        // exports take their arguments directly (sceKernelPollSema, SignalSema and
        // CancelSema take a handle and a count), so requiring exactly one parameter
        // would be wrong rather than strict.
        var malformed = AllExports()
            .SelectMany(method => method
                .GetCustomAttributes<SysAbiExportAttribute>()
                .Select(attribute => (Method: method, Attribute: attribute)))
            .Where(entry => !entry.Method.IsStatic ||
                           entry.Method.ReturnType != typeof(int) ||
                           entry.Method.GetParameters() is not [{ ParameterType: var first }, ..] ||
                           first != typeof(CpuContext))
            .Select(entry => $"{entry.Method.DeclaringType?.Name}.{entry.Method.Name}" +
                             $" ({entry.Attribute.LibraryName}, nid={entry.Attribute.Nid})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            malformed.Length == 0,
            $"{malformed.Length} export(s) are not static int (CpuContext, ...) and so cannot be " +
            $"dispatched: {string.Join(", ", malformed)}");
    }

    [Fact]
    public void NoNidIsBoundToTwoDifferentHandlers()
    {
        // A duplicate means the second registration silently wins, or the first is
        // unreachable, depending on lookup order.
        var duplicates = Gen5NidExports()
            .GroupBy(attribute => attribute.Nid, StringComparer.Ordinal)
            .Where(group => group
                .Select(attribute => attribute.LibraryName + ":" + attribute.ExportName)
                .Distinct()
                .Count() > 1)
            .Select(group => $"{group.Key} -> {string.Join(" and ", group
                .Select(attribute => attribute.LibraryName + ":" + attribute.ExportName)
                .Distinct()
                .OrderBy(text => text, StringComparer.Ordinal))}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            $"{duplicates.Length} NID(s) are registered to more than one export: "
            + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryExportIsReachableFromSomeLoadedAssembly()
    {
        // Guards the reflection above against silently shrinking to a scope that
        // finds nothing, which would make the checks above vacuously pass.
        var exports = AllExports().Count;
        Assert.True(exports > 100, $"only found {exports} HLE exports");

        Assert.True(
            Gen5NidExports().Count > 100,
            $"only found {Gen5NidExports().Count} Gen5 exports carrying a NID");
    }
}
