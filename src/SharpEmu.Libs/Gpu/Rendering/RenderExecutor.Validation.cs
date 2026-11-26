// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    private const uint AttributeWriteShadingRateHintToCmask = 1u << 31;
    private const uint DccControlOverwriteCombinerDisable = 1u << 0;
    private const uint DccControlKeyClearEnable = 1u << 1;

    // The register state a draw cannot run with; the target builders check the rest when they resolve.
    private void ValidateDrawRegisters(RegisterBanks banks)
    {
        var userVectors = banks.UserConfig.GeometryEngineUserVectorEnable;
        if (userVectors.VectorRegister1 || userVectors.VectorRegister2 || userVectors.VectorRegister3)
        {
            throw _host.Fatal($"Geometry engine user vector registers are not supported: v1={userVectors.VectorRegister1} v2={userVectors.VectorRegister2} v3={userVectors.VectorRegister3}.");
        }

        var context = banks.Context;
        var slot = ColorTargetResolver.FirstBound(context);
        ValidateColorTarget(in context.ColorTargets[slot], slot);
        if (context.ScanMode.LineStippleEnable)
        {
            throw _host.Fatal("Line stipple is not supported.");
        }

        var clip = context.Clip;
        if (clip.UserClipPlanes != 0 || clip.UserClipPlaneMode != 0 || clip.VertexKillAny || clip.UserClipPlaneNegateY ||
            clip.UserClipPlaneCullOnly || clip.CullOnClippingErrorDisable || clip.ForceViewportIndexFromVertexShader)
        {
            throw _host.Fatal(
                $"The clip control state is not supported: planes={clip.UserClipPlanes} mode={clip.UserClipPlaneMode} killAny={clip.VertexKillAny} " +
                $"negateY={clip.UserClipPlaneNegateY} cullOnly={clip.UserClipPlaneCullOnly} cullOnError={clip.CullOnClippingErrorDisable} viewportIndex={clip.ForceViewportIndexFromVertexShader}.");
        }

        var depth = context.DepthTarget;
        if (depth.CopyCentroid)
        {
            throw _host.Fatal($"Depth copy centroid is not supported: renderControl=0x{depth.RenderControl:X8}.");
        }

        if (depth.CopySample != 0)
        {
            throw _host.Fatal($"Depth copy sample is not supported: copySample={depth.CopySample} renderControl=0x{depth.RenderControl:X8}.");
        }

        var mode = context.RasterMode;
        // POLY_MODE 0 draws filled triangles whatever the per-face polygon types say. Point and line
        // face types are drawn filled: the pipelines have no polygon mode state, and a wireframe or
        // point pass (MK11 fights set front type 1) is cosmetic where a fatal ends the session.
        if (mode.PolygonMode != 0 && (mode.FrontPolygonType is not (0 or 2) || mode.BackPolygonType is not (0 or 2)) &&
            Interlocked.Exchange(ref _warnedPolygonTypes, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[GPU][WARN] Polygon types front={mode.FrontPolygonType} back={mode.BackPolygonType} are drawn filled.");
        }

        if (mode.ProvokingVertexLast)
        {
            throw _host.Fatal("The last provoking vertex is not supported.");
        }

        if (mode.PerspectiveCorrectionDisable)
        {
            throw _host.Fatal("Disabled perspective correction is not supported.");
        }
    }

    private static int _warnedPolygonTypes;

    private void ValidateColorTarget(in ColorTargetWords target, uint slot)
    {
        if (target.BaseAddress == 0)
        {
            return;
        }

        if (target.SliceStart > target.SliceMax)
        {
            throw _host.Fatal($"The color target slice range is inverted: slot={slot} start={target.SliceStart} last={target.SliceMax}.");
        }

        if (target.FmaskCompression && target.SamplesLog2 == 0 && target.FragmentsLog2 == 0)
        {
            throw _host.Fatal($"FMASK compression on a single-sample color target is not supported: slot={slot} info=0x{target.Info:X8}.");
        }

        if (target.FmaskCompressionDisabled)
        {
            throw _host.Fatal($"FMASK data compression disable is not supported: slot={slot} info=0x{target.Info:X8}.");
        }

        if ((target.Attrib3 & AttributeWriteShadingRateHintToCmask) != 0)
        {
            throw _host.Fatal($"Writing the shading rate hint to CMASK is not supported: slot={slot} attrib3=0x{target.Attrib3:X8}.");
        }

        if ((target.DccControl & DccControlOverwriteCombinerDisable) != 0)
        {
            throw _host.Fatal($"DCC overwrite combiner disable is not supported: slot={slot} dccControl=0x{target.DccControl:X8}.");
        }

        if ((target.DccControl & DccControlKeyClearEnable) != 0)
        {
            throw _host.Fatal($"DCC key clear is not supported: slot={slot} dccControl=0x{target.DccControl:X8}.");
        }
    }
}
