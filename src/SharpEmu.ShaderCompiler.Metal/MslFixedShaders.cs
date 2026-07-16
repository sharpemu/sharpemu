// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// The fixed presenter shaders, mirroring SpirvFixedShaders semantically. The
/// MSL lives in Templates/*.msl (authored as real Metal source); this class
/// only substitutes the per-call parameters. Entry point names are stable
/// (Metal forbids "main"); textures and samplers bind at index 0; attributes
/// use the same user(locn) convention as the translated stages.
/// </summary>
public static class MslFixedShaders
{
    /// <summary>
    /// Fullscreen triangle from the vertex index; every attribute location in
    /// 0..attributeCount-1 carries (x, y, 0, 1) so paired fragment stages can
    /// read a screen-space UV from any location.
    /// </summary>
    public static string CreateFullscreenVertex(uint attributeCount)
    {
        var fields = new StringBuilder();
        var stores = new StringBuilder();
        for (uint index = 0; index < attributeCount; index++)
        {
            if (index != 0)
            {
                fields.AppendLine();
                stores.AppendLine();
            }

            fields.Append($"    float4 attr{index} [[user(locn{index})]];");
            stores.Append($"    out.attr{index} = float4(x, y, 0.0f, 1.0f);");
        }

        return MslTemplates.Render(
            "fullscreen_vertex",
            ("attribute_fields", fields.ToString()),
            ("attribute_stores", stores.ToString()));
    }

    /// <summary>Samples texture 0 at the interpolated location-0 UV.</summary>
    public static string CreateCopyFragment() => MslTemplates.Render("copy_fragment");

    /// <summary>
    /// The presenter's blit stage: samples texture 0 with V flipped, because
    /// pairing the shared fullscreen triangle with Metal's y-up NDC puts UV
    /// (0,0) at the bottom of the screen while textures keep v=0 at the top.
    /// </summary>
    public static string CreatePresentFragment() => MslTemplates.Render("present_fragment");

    public static string CreateSolidFragment(float red, float green, float blue, float alpha) =>
        MslTemplates.Render(
            "solid_fragment",
            ("red", Format(red)),
            ("green", Format(green)),
            ("blue", Format(blue)),
            ("alpha", Format(alpha)));

    /// <summary>
    /// Diagnostic fragment stage exposing one interpolated vertex output
    /// directly as color, isolating fragment translation from interface data.
    /// </summary>
    public static string CreateAttributeFragment(uint location) =>
        MslTemplates.Render(
            "attribute_fragment",
            ("location", location.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Output-free fragment stage for fixed-function depth-only passes: the
    /// guest has no pixel shader, so no color may be written while depth
    /// testing still runs for the translated vertex shader.
    /// </summary>
    public static string CreateDepthOnlyFragment() => MslTemplates.Render("depth_only_fragment");

    private static string Format(float value) =>
        value.ToString("0.0######", CultureInfo.InvariantCulture) + "f";
}
