// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public enum Gen5TessellationDomain : uint
{
    Isolines,
    Triangles,
    Quads,
}

public enum Gen5TessellationSpacing : uint
{
    Equal,
    PowerOfTwo,
    FractionalOdd,
    FractionalEven,
}

// The merged LS/HS ABI supplies separate vertex and control-point lane counts.
// HullEntryPc names the first HS instruction in the decoded fused program.
public readonly record struct Gen5TessellationHullInfo(
    uint InputControlPoints, uint OutputControlPoints, uint PatchesPerGroup, uint HullEntryPc)
{
    public static bool TryDecode(uint configuration, uint hullEntryPc,
        out Gen5TessellationHullInfo info, out string error)
    {
        // VGT_LS_HS_CONFIG stores counts directly, rather than count minus one.
        info = new((configuration >> 8) & 0x3F, (configuration >> 14) & 0x3F,
            configuration & 0xFF, hullEntryPc);
        if (info.InputControlPoints is 0 or > 32 || info.OutputControlPoints is 0 or > 32 ||
            info.PatchesPerGroup is 0 or > 256 ||
            info.PatchesPerGroup * Math.Max(info.InputControlPoints, info.OutputControlPoints) > 256)
        {
            error = "Invalid merged tessellation workgroup configuration.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}

// VGT_TF_PARAM describes the fixed-function tessellator, independently of
// where the guest stores control points and tessellation factors.
public readonly record struct Gen5TessellationInfo(
    Gen5TessellationDomain Domain,
    Gen5TessellationSpacing Spacing,
    bool PointMode,
    bool Clockwise)
{
    public static bool TryDecode(uint parameter, out Gen5TessellationInfo info, out string error)
    {
        var domain = parameter & 3;
        var spacing = (parameter >> 2) & 7;
        var topology = (parameter >> 5) & 7;
        info = default;
        if (domain > 2 || spacing > 3 || topology > 3 ||
            (topology == 1 && domain != 0) || (topology >= 2 && domain == 0))
        {
            error = $"Invalid tessellator configuration: type={domain} partitioning={spacing} topology={topology}.";
            return false;
        }
        info = new((Gen5TessellationDomain)domain, (Gen5TessellationSpacing)spacing,
            topology == 0, topology == 2);
        error = string.Empty;
        return true;
    }
}
