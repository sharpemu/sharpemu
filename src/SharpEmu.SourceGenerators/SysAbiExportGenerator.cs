// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Emits the SysAbi export registry at compile time: one static class per assembly whose
/// CreateExports(Generation) reproduces ModuleManager.RegisterFromAssembly's reflection
/// scan — same generation filtering, same name fallback, same library default — as plain
/// method-group registrations. NIDs omitted from attributes are derived from the export
/// name with the PS NID algorithm (the same computation the runtime symbol catalog was
/// built from).
///
/// Invalid declarations are skipped here and rejected by SysAbiExportAnalyzer as build
/// errors, so nothing can be silently dropped.
/// </summary>
[Generator]
public sealed class SysAbiExportGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "SharpEmu.HLE.SysAbiExportAttribute";

    private sealed class ExportModel : IEquatable<ExportModel>
    {
        public ExportModel(string containingType, string methodName, bool hasContextParameter, string libraryName, string nid, string exportName, int target)
        {
            ContainingType = containingType;
            MethodName = methodName;
            HasContextParameter = hasContextParameter;
            LibraryName = libraryName;
            Nid = nid;
            ExportName = exportName;
            Target = target;
        }

        public string ContainingType { get; }
        public string MethodName { get; }
        public bool HasContextParameter { get; }
        public string LibraryName { get; }
        public string Nid { get; }
        public string ExportName { get; }
        public int Target { get; }

        public bool Equals(ExportModel? other) =>
            other is not null &&
            ContainingType == other.ContainingType &&
            MethodName == other.MethodName &&
            HasContextParameter == other.HasContextParameter &&
            LibraryName == other.LibraryName &&
            Nid == other.Nid &&
            ExportName == other.ExportName &&
            Target == other.Target;

        public override bool Equals(object? obj) => Equals(obj as ExportModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + ContainingType.GetHashCode();
                hash = (hash * 31) + MethodName.GetHashCode();
                hash = (hash * 31) + Nid.GetHashCode();
                return hash;
            }
        }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var exports = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (attributeContext, _) => CreateModel(attributeContext))
            .Where(static model => model is not null)
            .Collect();

        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(
            exports.Combine(assemblyName),
            static (productionContext, source) => Emit(productionContext, source.Left!, source.Right));
    }

    private static ExportModel? CreateModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method ||
            !SysAbiExportShape.IsValidHandler(method) ||
            !SysAbiExportShape.IsAccessibleFromGeneratedCode(method))
        {
            return null;
        }

        var attribute = context.Attributes[0];
        var arguments = SysAbiExportShape.ReadArguments(attribute);
        var nid = arguments.Nid;
        var exportName = arguments.ExportName;

        // Mirror ModuleManager.ResolveExportInfo: a missing NID resolves from the export
        // name (algorithmically — equivalent to the runtime catalog lookup, which was
        // built with the same computation); a missing name falls back to the method name.
        if (string.IsNullOrWhiteSpace(nid) && !string.IsNullOrWhiteSpace(exportName))
        {
            nid = Ps5Nid.Compute(exportName);
        }

        if (string.IsNullOrWhiteSpace(nid))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(exportName))
        {
            exportName = method.Name;
        }

        var libraryName = string.IsNullOrWhiteSpace(arguments.LibraryName) ? "libKernel" : arguments.LibraryName;
        return new ExportModel(
            method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            method.Name,
            method.Parameters.Length == 1,
            libraryName,
            nid!,
            exportName!,
            arguments.Target);
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ExportModel?> exports,
        string assemblyName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated by SharpEmu.SourceGenerators/SysAbiExportGenerator />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace SharpEmu.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Compile-time SysAbi export registry for " + assemblyName + ".</summary>");
        builder.AppendLine("public static class SysAbiExportRegistry");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Exports effective for the given registration generation, with the same");
        builder.AppendLine("    /// semantics as the reflection scan: an attribute Target of None inherits the");
        builder.AppendLine("    /// registration generation, and exports outside it are skipped.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<global::SharpEmu.HLE.ExportedFunction> CreateExports(");
        builder.AppendLine("        global::SharpEmu.HLE.Generation registrationGeneration)");
        builder.AppendLine("    {");
        builder.AppendLine($"        var exports = new global::System.Collections.Generic.List<global::SharpEmu.HLE.ExportedFunction>({exports.Length});");

        foreach (var export in exports)
        {
            if (export is null)
            {
                continue;
            }

            var function = export.HasContextParameter
                ? $"{export.ContainingType}.{export.MethodName}"
                : $"static _ => {export.ContainingType}.{export.MethodName}()";
            builder.AppendLine(
                $"        Add(exports, registrationGeneration, {Literal(export.LibraryName)}, {Literal(export.Nid)}, " +
                $"{Literal(export.ExportName)}, (global::SharpEmu.HLE.Generation){export.Target}, {function});");
        }

        builder.AppendLine("        return exports;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static void Add(");
        builder.AppendLine("        global::System.Collections.Generic.List<global::SharpEmu.HLE.ExportedFunction> exports,");
        builder.AppendLine("        global::SharpEmu.HLE.Generation registrationGeneration,");
        builder.AppendLine("        string libraryName,");
        builder.AppendLine("        string nid,");
        builder.AppendLine("        string exportName,");
        builder.AppendLine("        global::SharpEmu.HLE.Generation attributeTarget,");
        builder.AppendLine("        global::SharpEmu.HLE.SysAbiFunction function)");
        builder.AppendLine("    {");
        builder.AppendLine("        var target = attributeTarget == global::SharpEmu.HLE.Generation.None ? registrationGeneration : attributeTarget;");
        builder.AppendLine("        if ((target & registrationGeneration) == 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            return;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        exports.Add(new global::SharpEmu.HLE.ExportedFunction(libraryName, nid, exportName, target, function));");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("SysAbiExportRegistry.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
