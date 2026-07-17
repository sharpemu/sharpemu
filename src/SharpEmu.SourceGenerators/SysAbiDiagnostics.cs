// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Compile-time rules for [SysAbiExport] declarations. Everything here used to be a
/// runtime discovery (console warning or InvalidOperationException at boot); the
/// analyzer turns each one into a build failure.
/// </summary>
public static class SysAbiDiagnostics
{
    private const string Category = "SharpEmu.SysAbi";

    public static readonly DiagnosticDescriptor DuplicateNid = new(
        "SHEM001",
        "Duplicate SysAbi NID",
        "NID '{0}' is exported by both '{1}' and '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidNidFormat = new(
        "SHEM002",
        "Invalid NID format",
        "NID '{0}' is not eleven characters of the PS base64 alphabet (A-Z a-z 0-9 + -)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidHandlerSignature = new(
        "SHEM003",
        "Invalid SysAbi handler signature",
        "Method '{0}' must be a static, non-generic method returning int and taking no parameters, a single CpuContext parameter, or a CpuContext followed by up to six int/uint/long/ulong or [GuestCString] string parameters",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NidNameMismatch = new(
        "SHEM004",
        "NID does not match export name",
        "NID '{0}' does not match export name '{1}' (computed NID is '{2}') — one of the two is wrong",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnresolvableExport = new(
        "SHEM005",
        "Export declares neither NID nor export name",
        "Method '{0}' must declare an ExportName (from which the NID is derived) or an explicit Nid",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NameNotInCatalog = new(
        "SHEM006",
        "Export name not present in the PS5 symbol catalog",
        "Export name '{0}' is not in ps5_names.txt — likely a typo, or the catalog needs the new symbol",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor HandlerNotAccessible = new(
        "SHEM007",
        "SysAbi handler not accessible to generated registration",
        "Method '{0}' (or its containing type) must be at least internal so the generated registry can reference it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidGuestCString = new(
        "SHEM008",
        "Invalid [GuestCString] usage",
        "Method '{0}' misuses [GuestCString]: it applies only to string parameters and requires a positive MaxLength",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
