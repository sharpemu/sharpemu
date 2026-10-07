// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

// Internal resolution scaling. The host may hold a guest render target at another size, so
// everything the program expresses in pixels has to be translated: its own pixel position,
// the integer coordinates it addresses an image with and the sizes a resource query reports.
// Which images are scaled is a per-draw fact, so the factors arrive in shader data rather
// than through a permutation.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // Shader-data dwords, relative to BindingLayout.RenderScaleDword.
        private const uint ImageCoordinateFactorDword = 0;
        private const uint ImageSizeFactorDword = 1;
        private const uint ScaledImageMaskLowDword = 2;
        private const uint ScaledImageMaskHighDword = 3;
        private const uint PixelPositionFactorDword = 4;

        private bool UsesRenderScale => _request.Bindings.UsesRenderScale;

        private uint LoadRenderScaleFloat(uint dword) =>
            Bitcast(_floatType, LoadShaderDataDword(UInt(_request.Bindings.RenderScaleDword + dword)));

        // One when the host holds this image at guest resolution, the scale otherwise. The
        // mask is loaded per use so the value never has to dominate a later block.
        private uint LoadImageScaleFactor(in SpirvImageResource resource, uint factorDword)
        {
            var index = resource.ResourceIndex;
            var maskDword = index < 32 ? ScaledImageMaskLowDword : ScaledImageMaskHighDword;
            var bit = UInt(1u << (int)(index & 31));
            var mask = LoadShaderDataDword(UInt(_request.Bindings.RenderScaleDword + maskDword));
            var scaled = _module.AddInstruction(SpirvOp.INotEqual, _boolType,
                _module.AddInstruction(SpirvOp.BitwiseAnd, _uintType, mask, bit), UInt(0));
            return _module.AddInstruction(SpirvOp.Select, _floatType, scaled, LoadRenderScaleFloat(factorDword), Float(1f));
        }

        // The pixel position addresses host texels; dividing x and y by the attachments'
        // factor hands the program the guest pixel it would have shaded natively.
        private uint ScalePixelPosition(uint fragCoord)
        {
            if (!UsesRenderScale)
            {
                return fragCoord;
            }

            var factor = LoadRenderScaleFloat(PixelPositionFactorDword);
            var components = new uint[4];
            for (uint component = 0; component < 4; component++)
            {
                var value = _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, fragCoord, component);
                components[component] = component < 2
                    ? _module.AddInstruction(SpirvOp.FMul, _floatType, value, factor)
                    : value;
            }

            return _module.AddInstruction(SpirvOp.CompositeConstruct, _vec4Type, components);
        }

        // A guest pixel coordinate in host texels. The program computed it against the guest
        // size, so it is multiplied by the factor and truncated back to a texel index.
        private uint ScaleIntegerImageCoordinate(uint coordinate, in SpirvImageResource resource)
        {
            if (!UsesRenderScale)
            {
                return coordinate;
            }

            var factor = LoadImageScaleFactor(resource, ImageCoordinateFactorDword);
            var scaled = _module.AddInstruction(SpirvOp.FMul, _floatType,
                _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, coordinate), factor);
            return _module.AddInstruction(SpirvOp.ConvertFToS, _intType, scaled);
        }

        // A resource query reports the size the game declared, not the host's: the program
        // derives texture coordinates and loop bounds from it in guest pixels.
        private uint ScaleImageQuerySize(uint size, in SpirvImageResource resource)
        {
            if (!UsesRenderScale)
            {
                return size;
            }

            var factor = LoadImageScaleFactor(resource, ImageSizeFactorDword);
            var scaled = _module.AddInstruction(SpirvOp.FAdd, _floatType,
                _module.AddInstruction(SpirvOp.FMul, _floatType,
                    _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, size), factor),
                Float(0.5f));
            return _module.AddInstruction(SpirvOp.ConvertFToS, _intType, scaled);
        }
    }
}
