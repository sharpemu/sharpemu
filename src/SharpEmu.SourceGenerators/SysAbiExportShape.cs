// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Shared shape rules for [SysAbiExport] methods, used by both the generator (to decide
/// what it can emit) and the analyzer (to reject everything else as build errors), so
/// the two can never disagree about what a valid handler is.
/// </summary>
public static class SysAbiExportShape
{
    public readonly struct Arguments
    {
        public Arguments(string libraryName, string nid, string exportName, int target)
        {
            LibraryName = libraryName;
            Nid = nid;
            ExportName = exportName;
            Target = target;
        }

        public string LibraryName { get; }
        public string Nid { get; }
        public string ExportName { get; }
        public int Target { get; }
    }

    /// <summary>Static, non-generic, returns int, takes () or (CpuContext).</summary>
    public static bool IsValidHandler(IMethodSymbol method)
    {
        if (!method.IsStatic ||
            method.IsGenericMethod ||
            method.ReturnType.SpecialType != SpecialType.System_Int32)
        {
            return false;
        }

        if (method.Parameters.Length == 0)
        {
            return true;
        }

        return method.Parameters.Length == 1 &&
            method.Parameters[0].RefKind == RefKind.None &&
            method.Parameters[0].Type.ToDisplayString() == "SharpEmu.HLE.CpuContext";
    }

    /// <summary>The generated registry lives in the same assembly: internal suffices.</summary>
    public static bool IsAccessibleFromGeneratedCode(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility is Accessibility.Private or Accessibility.ProtectedOrInternal
            or Accessibility.Protected or Accessibility.ProtectedAndInternal)
        {
            return false;
        }

        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }

        return true;
    }

    public static Arguments ReadArguments(AttributeData attribute)
    {
        var libraryName = string.Empty;
        var nid = string.Empty;
        var exportName = string.Empty;
        var target = 0;
        foreach (var argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "LibraryName":
                    libraryName = argument.Value.Value as string ?? string.Empty;
                    break;
                case "Nid":
                    nid = argument.Value.Value as string ?? string.Empty;
                    break;
                case "ExportName":
                    exportName = argument.Value.Value as string ?? string.Empty;
                    break;
                case "Target":
                    target = argument.Value.Value is int value ? value : 0;
                    break;
            }
        }

        return new Arguments(libraryName, nid, exportName, target);
    }
}
