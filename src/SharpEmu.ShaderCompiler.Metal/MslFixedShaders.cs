// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// The fixed presenter shaders, mirroring SpirvFixedShaders semantically: MSL
/// source text with stable entry point names (Metal forbids "main"). Textures
/// and samplers bind at index 0; attributes use the same user(locn) convention
/// as the translated stages.
/// </summary>
public static class MslFixedShaders
{
    private const string Prologue = """
        #include <metal_stdlib>

        using namespace metal;

        """;

    /// <summary>
    /// Fullscreen triangle from the vertex index; every attribute location in
    /// 0..attributeCount-1 carries (x, y, 0, 1) so paired fragment stages can
    /// read a screen-space UV from any location.
    /// </summary>
    public static string CreateFullscreenVertex(uint attributeCount)
    {
        var source = new System.Text.StringBuilder(Prologue);
        source.AppendLine("struct FullscreenOut");
        source.AppendLine("{");
        source.AppendLine("    float4 position [[position]];");
        for (uint index = 0; index < attributeCount; index++)
        {
            source.AppendLine($"    float4 attr{index} [[user(locn{index})]];");
        }

        source.AppendLine("};");
        source.AppendLine();
        source.AppendLine("vertex FullscreenOut fullscreen_vs(uint vertex_id [[vertex_id]])");
        source.AppendLine("{");
        source.AppendLine("    float x = (float)((vertex_id << 1) & 2u);");
        source.AppendLine("    float y = (float)(vertex_id & 2u);");
        source.AppendLine("    FullscreenOut out = {};");
        source.AppendLine("    out.position = float4(x * 2.0f - 1.0f, y * 2.0f - 1.0f, 0.0f, 1.0f);");
        for (uint index = 0; index < attributeCount; index++)
        {
            source.AppendLine($"    out.attr{index} = float4(x, y, 0.0f, 1.0f);");
        }

        source.AppendLine("    return out;");
        source.AppendLine("}");
        return source.ToString();
    }

    /// <summary>Samples texture 0 at the interpolated location-0 UV.</summary>
    public static string CreateCopyFragment() =>
        Prologue +
        """
        struct CopyIn
        {
            float4 attr0 [[user(locn0)]];
        };

        fragment float4 copy_fs(
            CopyIn in [[stage_in]],
            texture2d<float> tex0 [[texture(0)]],
            sampler smp0 [[sampler(0)]])
        {
            return tex0.sample(smp0, in.attr0.xy);
        }
        """;

    public static string CreateSolidFragment(float red, float green, float blue, float alpha) =>
        Prologue +
        "fragment float4 solid_fs()\n" +
        "{\n" +
        $"    return float4({Format(red)}, {Format(green)}, {Format(blue)}, {Format(alpha)});\n" +
        "}\n";

    /// <summary>
    /// Diagnostic fragment stage exposing one interpolated vertex output
    /// directly as color, isolating fragment translation from interface data.
    /// </summary>
    public static string CreateAttributeFragment(uint location) =>
        Prologue +
        "struct AttributeIn\n" +
        "{\n" +
        $"    float4 attr{location} [[user(locn{location})]];\n" +
        "};\n" +
        "\n" +
        "fragment float4 attribute_fs(AttributeIn in [[stage_in]])\n" +
        "{\n" +
        $"    return in.attr{location};\n" +
        "}\n";

    /// <summary>
    /// Output-free fragment stage for fixed-function depth-only passes: the
    /// guest has no pixel shader, so no color may be written while depth
    /// testing still runs for the translated vertex shader.
    /// </summary>
    public static string CreateDepthOnlyFragment() =>
        Prologue +
        """
        fragment void depth_only_fs()
        {
        }
        """;

    private static string Format(float value) =>
        value.ToString("0.0######", CultureInfo.InvariantCulture) + "f";
}
