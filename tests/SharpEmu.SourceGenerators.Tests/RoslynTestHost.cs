// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SharpEmu.HLE;

namespace SharpEmu.SourceGenerators.Tests;

/// <summary>
/// Builds in-memory compilations for driving the generator and analyzer: the test-host
/// runtime assemblies plus the real SharpEmu.HLE (for SysAbiExportAttribute,
/// ExportedFunction, and friends), so generated output is compiled against the exact
/// types it will target in the emulator.
/// </summary>
internal static class RoslynTestHost
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(static () =>
    {
        var references = new List<MetadataReference>();
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length != 0)
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        references.Add(MetadataReference.CreateFromFile(typeof(SysAbiExportAttribute).Assembly.Location));
        return references;
    });

    public static CSharpCompilation Compile(params string[] sources)
    {
        var trees = new SyntaxTree[sources.Length];
        for (var index = 0; index < sources.Length; index++)
        {
            trees[index] = CSharpSyntaxTree.ParseText(
                sources[index],
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        }

        return CSharpCompilation.Create(
            "SysAbiGeneratorTests",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static (Compilation Updated, string GeneratedSource) RunGenerator(CSharpCompilation compilation)
    {
        var driver = CSharpGeneratorDriver.Create(new SysAbiExportGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var generated = string.Empty;
        foreach (var tree in updated.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith("SysAbiExportRegistry.g.cs", StringComparison.Ordinal))
            {
                generated = tree.ToString();
            }
        }

        return (updated, generated);
    }

    public static IReadOnlyList<Diagnostic> RunAnalyzer(
        CSharpCompilation compilation,
        params AdditionalText[] additionalFiles)
    {
        var withAnalyzers = compilation.WithAnalyzers(
            [new SysAbiExportAnalyzer()],
            new AnalyzerOptions([.. additionalFiles]));
        var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        var result = new List<Diagnostic>(diagnostics.Length);
        foreach (var diagnostic in diagnostics)
        {
            result.Add(diagnostic);
        }

        return result;
    }

    public static void AssertCompiles(Compilation compilation)
    {
        var errors = new List<string>();
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(diagnostic.ToString());
            }
        }

        Xunit.Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}

internal sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) =>
        SourceText.From(content);
}
