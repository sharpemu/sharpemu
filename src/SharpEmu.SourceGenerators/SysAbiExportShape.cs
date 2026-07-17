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
    /// <summary>Single source of truth so the generator and analyzer can never
    /// disagree about which attribute marks an export.</summary>
    public const string SysAbiExportAttributeName = "SharpEmu.HLE.SysAbiExportAttribute";

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

    /// <summary>
    /// SysV integer argument registers in call order; typed handler parameters map to
    /// these positionally.
    /// </summary>
    public static readonly string[] ArgumentRegisters = ["Rdi", "Rsi", "Rdx", "Rcx", "R8", "R9"];

    public enum HandlerShape
    {
        Invalid,
        /// <summary>int M(CpuContext) — the classic raw-register shape.</summary>
        ContextOnly,
        /// <summary>int M() — no guest state needed.</summary>
        Parameterless,
        /// <summary>int M(CpuContext, up to six int/uint/long/ulong args) — the
        /// generator emits the SysV register unmarshalling thunk.</summary>
        Typed,
    }

    private const string GuestCStringAttributeName = "SharpEmu.HLE.GuestCStringAttribute";

    /// <summary>Static, non-generic, returns int, takes one of the supported shapes.</summary>
    public static HandlerShape Classify(IMethodSymbol method, out string typedParameterKinds) =>
        Classify(method, out typedParameterKinds, out _);

    /// <summary>
    /// <paramref name="invalidGuestCString"/> distinguishes a misused [GuestCString]
    /// (wrong parameter type, non-positive MaxLength) from a plain signature mismatch,
    /// so the analyzer can point at the marshalling attribute instead of the shape.
    /// </summary>
    public static HandlerShape Classify(IMethodSymbol method, out string typedParameterKinds, out bool invalidGuestCString)
    {
        typedParameterKinds = string.Empty;
        invalidGuestCString = false;
        if (!method.IsStatic ||
            method.IsGenericMethod ||
            method.ReturnType.SpecialType != SpecialType.System_Int32)
        {
            return HandlerShape.Invalid;
        }

        if (method.Parameters.Length == 0)
        {
            return HandlerShape.Parameterless;
        }

        if (method.Parameters[0].RefKind != RefKind.None || !IsCpuContext(method.Parameters[0].Type))
        {
            return HandlerShape.Invalid;
        }

        if (method.Parameters.Length == 1)
        {
            return HandlerShape.ContextOnly;
        }

        if (method.Parameters.Length > 1 + ArgumentRegisters.Length)
        {
            return HandlerShape.Invalid;
        }

        var kinds = new string[method.Parameters.Length - 1];
        for (var index = 1; index < method.Parameters.Length; index++)
        {
            var parameter = method.Parameters[index];
            if (parameter.RefKind != RefKind.None)
            {
                return HandlerShape.Invalid;
            }

            var hasGuestCString = TryGetGuestCStringMaxLength(parameter, out var maxLength);
            if (parameter.Type.SpecialType == SpecialType.System_String)
            {
                if (!hasGuestCString)
                {
                    // A bare string has no register representation; the guest pointer
                    // must be marshalled explicitly via [GuestCString].
                    return HandlerShape.Invalid;
                }

                if (maxLength <= 0)
                {
                    invalidGuestCString = true;
                    return HandlerShape.Invalid;
                }

                kinds[index - 1] = "cstring:" + maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (hasGuestCString)
            {
                invalidGuestCString = true;
                return HandlerShape.Invalid;
            }

            kinds[index - 1] = parameter.Type.SpecialType switch
            {
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_Int64 => "long",
                SpecialType.System_UInt64 => "ulong",
                _ => string.Empty,
            };
            if (kinds[index - 1].Length == 0)
            {
                return HandlerShape.Invalid;
            }
        }

        typedParameterKinds = string.Join(",", kinds);
        return HandlerShape.Typed;
    }

    // Symbol names are compared with an explicit fully-qualified format so
    // classification can never depend on a display-format default.
    private static bool IsCpuContext(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::SharpEmu.HLE.CpuContext";

    public static bool IsSysAbiExportAttribute(INamedTypeSymbol? attributeClass) =>
        attributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + SysAbiExportAttributeName;

    private static bool TryGetGuestCStringMaxLength(IParameterSymbol parameter, out int maxLength)
    {
        maxLength = 0;
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::" + GuestCStringAttributeName)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int value)
            {
                maxLength = value;
            }

            return true;
        }

        return false;
    }

    public static bool IsValidHandler(IMethodSymbol method) =>
        Classify(method, out _) != HandlerShape.Invalid;

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
