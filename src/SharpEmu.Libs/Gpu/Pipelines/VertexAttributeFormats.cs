// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The host vertex format of a buffer descriptor's format, narrowed to the components the program fetches.
public static class VertexAttributeFormats
{
    private const uint AcceptedSignedShortPairFormat = 113;
    private const uint AcceptedHalfPairFormat = 121;
    private static int _narrowingLines;
    private static int _acceptedFormat113Lines;
    private static int _acceptedFormat121Lines;

    // The format and its component count; false when the descriptor format is unknown.
    public static bool TryResolve(in BufferDescriptorWords descriptor, uint usedComponents, out Format format, out uint size)
    {
        var rawFormat = descriptor.Format;
        if (rawFormat == AcceptedSignedShortPairFormat)
        {
            if (Interlocked.Exchange(ref _acceptedFormat113Lines, 1) == 0)
            {
                Console.Error.WriteLine("[LOADER][INFO] vertex_input accepted buffer format 113 as R32G32B32A32_SFLOAT");
            }

            format = Format.R32G32B32A32Sfloat;
            size = 4;
            Narrow(ref format, ref size, usedComponents);
            return true;
        }

        if (rawFormat == AcceptedHalfPairFormat)
        {
            if (Interlocked.Exchange(ref _acceptedFormat121Lines, 1) == 0)
            {
                Console.Error.WriteLine("[LOADER][INFO] vertex_input accepted buffer format 121 as R16G16_SFLOAT");
            }

            format = Format.R16G16Sfloat;
            size = 2;
            Narrow(ref format, ref size, usedComponents);
            return true;
        }

        (format, size) = rawFormat switch
        {
            77 => (Format.R32G32B32A32Sfloat, 4u),
            76 => (Format.R32G32B32A32Sint, 4u),
            75 => (Format.R32G32B32A32Uint, 4u),
            74 => (Format.R32G32B32Sfloat, 3u),
            73 => (Format.R32G32B32Sint, 3u),
            72 => (Format.R32G32B32Uint, 3u),
            71 => (Format.R16G16B16A16Sfloat, 4u),
            70 => (Format.R16G16B16A16Sint, 4u),
            69 => (Format.R16G16B16A16Uint, 4u),
            68 => (Format.R16G16B16A16Sscaled, 4u),
            67 => (Format.R16G16B16A16Uscaled, 4u),
            66 => (Format.R16G16B16A16SNorm, 4u),
            65 => (Format.R16G16B16A16Unorm, 4u),
            64 => (Format.R32G32Sfloat, 2u),
            63 => (Format.R32G32Sint, 2u),
            62 => (Format.R32G32Uint, 2u),
            60 => (Format.R8G8B8A8Uint, 4u),
            59 => (Format.R8G8B8A8Sscaled, 4u),
            58 => (Format.R8G8B8A8Uscaled, 4u),
            57 => (Format.R8G8B8A8SNorm, 4u),
            56 => (Format.R8G8B8A8Unorm, 4u),
            50 => (Format.A2B10G10R10UnormPack32, 4u),
            51 => (Format.A2B10G10R10SNormPack32, 4u),
            29 => (Format.R16G16Sfloat, 2u),
            28 => (Format.R16G16Sint, 2u),
            27 => (Format.R16G16Uint, 2u),
            26 => (Format.R16G16Sscaled, 2u),
            25 => (Format.R16G16Uscaled, 2u),
            24 => (Format.R16G16SNorm, 2u),
            23 => (Format.R16G16Unorm, 2u),
            22 => (Format.R32Sfloat, 1u),
            21 => (Format.R32Sint, 1u),
            20 => (Format.R32Uint, 1u),
            19 => (Format.R8G8Sint, 2u),
            18 => (Format.R8G8Uint, 2u),
            17 => (Format.R8G8Sscaled, 2u),
            16 => (Format.R8G8Uscaled, 2u),
            15 => (Format.R8G8SNorm, 2u),
            14 => (Format.R8G8Unorm, 2u),
            13 => (Format.R16Sfloat, 1u),
            12 => (Format.R16Sint, 1u),
            11 => (Format.R16Uint, 1u),
            10 => (Format.R16Sscaled, 1u),
            9 => (Format.R16Uscaled, 1u),
            8 => (Format.R16SNorm, 1u),
            7 => (Format.R16Unorm, 1u),
            6 => (Format.R8Sint, 1u),
            5 => (Format.R8Uint, 1u),
            4 => (Format.R8Sscaled, 1u),
            3 => (Format.R8Uscaled, 1u),
            2 => (Format.R8SNorm, 1u),
            1 => (Format.R8Unorm, 1u),
            _ => (Format.Undefined, 4u),
        };
        if (format == Format.Undefined)
        {
            return false;
        }

        if (Narrow(ref format, ref size, usedComponents) && Interlocked.Increment(ref _narrowingLines) <= 32)
        {
            Console.Error.WriteLine($"[LOADER][INFO] vertex_input narrowed a vertex format to {usedComponents} component(s) for the program's fetch");
        }

        return true;
    }

    public static Format Resolve(in BufferDescriptorWords descriptor, uint usedComponents, out uint size)
    {
        if (!TryResolve(in descriptor, usedComponents, out var format, out size))
        {
            throw SubmissionScheduler.Fatal($"The vertex buffer format is unknown: format={descriptor.Format}.");
        }

        return format;
    }

    // Fewer components than the format holds select the matching narrower format.
    public static bool Narrow(ref Format format, ref uint size, uint usedComponents)
    {
        if (usedComponents == 0 || usedComponents >= size)
        {
            return false;
        }

        Format? narrowed = (format, usedComponents) switch
        {
            (Format.R32G32B32A32Sfloat, 1) => Format.R32Sfloat,
            (Format.R32G32B32A32Sfloat, 2) => Format.R32G32Sfloat,
            (Format.R32G32B32A32Sfloat, 3) => Format.R32G32B32Sfloat,
            (Format.R32G32B32Sfloat, 1) => Format.R32Sfloat,
            (Format.R32G32B32Sfloat, 2) => Format.R32G32Sfloat,
            (Format.R16G16B16A16Sfloat, 1) => Format.R16Sfloat,
            (Format.R16G16B16A16Sfloat, 2) => Format.R16G16Sfloat,
            (Format.R8G8B8A8Unorm, 1) => Format.R8Unorm,
            (Format.R8G8B8A8Unorm, 2) => Format.R8G8Unorm,
            (Format.R8G8B8A8SNorm, 2) => Format.R8G8SNorm,
            (Format.R8G8B8A8Uint, 1) => Format.R8Uint,
            (Format.R8G8B8A8Uint, 2) => Format.R8G8Uint,
            _ => null,
        };
        if (narrowed is null)
        {
            return false;
        }

        format = narrowed.Value;
        size = usedComponents;
        return true;
    }
}
