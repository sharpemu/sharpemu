// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial prepares guest vertex inputs for translated Vulkan draws.

    private sealed partial class Presenter
    {
        private int _tracedVertexBufferCount;
        private bool _tracedTitleDraw;
        private sealed class VertexBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public bool OwnsBuffer;
            public ulong Size;
            public uint Location;
            public uint ComponentCount;
            public uint DataFormat;
            public uint NumberFormat;
            public uint Stride;
            public uint OffsetBytes;
            public bool PerInstance;
        }

        private static bool IsTitleDraw(IReadOnlyList<GuestVertexBuffer> vertexBuffers)
        {
            foreach (var buffer in vertexBuffers)
            {
                if (buffer.Location == 0 &&
                    buffer.ComponentCount == 4 &&
                    buffer.DataFormat == 10 &&
                    buffer.NumberFormat == 0 &&
                    buffer.Stride == 16 &&
                    buffer.OffsetBytes == 12 &&
                    buffer.Length == 67568)
                {
                    return true;
                }
            }

            return false;
        }

        private VertexBufferResource CreateVertexBufferResource(
            GuestVertexBuffer guestBuffer)
        {
            ReadOnlySpan<byte> source = guestBuffer.Data.AsSpan(0, guestBuffer.Length);
            byte[]? forcedVertexColors = null;
            if (_forceTitleVertexColorWhite &&
                guestBuffer.Location == 0 &&
                guestBuffer.ComponentCount == 4 &&
                guestBuffer.DataFormat == 10 &&
                guestBuffer.NumberFormat == 0 &&
                guestBuffer.Stride == 16 &&
                guestBuffer.OffsetBytes == 12 &&
                guestBuffer.Length == 67568)
            {
                forcedVertexColors = source.ToArray();
                for (var offset = 12; offset + 3 < forcedVertexColors.Length; offset += 16)
                {
                    forcedVertexColors[offset] = 0xFF;
                    forcedVertexColors[offset + 1] = 0xFF;
                    forcedVertexColors[offset + 2] = 0xFF;
                }

                source = forcedVertexColors;
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.vertex_force_title_color_white " +
                    $"base=0x{guestBuffer.BaseAddress:X16} bytes={guestBuffer.Length}");
            }
            var buffer = CreateHostBuffer(
                source,
                BufferUsageFlags.VertexBufferBit,
                out var memory,
                out _);
            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            if (_setDebugUtilsObjectName is not null)
            {
                SetDebugName(
                    ObjectType.Buffer,
                    buffer.Handle,
                    $"SharpEmu vertex loc{guestBuffer.Location} " +
                    $"0x{guestBuffer.BaseAddress:X16} {guestBuffer.Length}b");
            }
            if (_tracedVertexBufferCount++ < 64)
            {
                TraceVulkanShader(
                    $"vk.vertex_buffer loc={guestBuffer.Location} " +
                    $"base=0x{guestBuffer.BaseAddress:X16} stride={guestBuffer.Stride} " +
                    $"offset={guestBuffer.OffsetBytes} comps={guestBuffer.ComponentCount} " +
                    $"fmt={guestBuffer.DataFormat}/num={guestBuffer.NumberFormat} " +
                    $"bytes={guestBuffer.Length}");
            }

            return CreateVertexBufferResource(
                buffer,
                memory,
                size,
                guestBuffer,
                ownsBuffer: true);
        }

        private static VertexBufferResource CreateVertexBufferResource(
            VkBuffer buffer,
            DeviceMemory memory,
            ulong size,
            GuestVertexBuffer guestBuffer,
            bool ownsBuffer)
        {
            return new VertexBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                OwnsBuffer = ownsBuffer,
                Size = size,
                Location = guestBuffer.Location,
                ComponentCount = guestBuffer.ComponentCount,
                DataFormat = guestBuffer.DataFormat,
                NumberFormat = guestBuffer.NumberFormat,
                Stride = guestBuffer.Stride,
                OffsetBytes = guestBuffer.OffsetBytes,
                PerInstance = guestBuffer.PerInstance,
            };
        }

        private static VertexBufferResource CreateVertexBufferAlias(
            VertexBufferResource shared,
            GuestVertexBuffer guestBuffer) => new()
        {
            Buffer = shared.Buffer,
            Memory = shared.Memory,
            OwnsBuffer = false,
            Size = shared.Size,
            Location = guestBuffer.Location,
            ComponentCount = guestBuffer.ComponentCount,
            DataFormat = guestBuffer.DataFormat,
            NumberFormat = guestBuffer.NumberFormat,
            Stride = guestBuffer.Stride,
            OffsetBytes = guestBuffer.OffsetBytes,
            PerInstance = guestBuffer.PerInstance,
        };

        private static PrimitiveTopology GetPrimitiveTopology(
            uint primitiveType,
            bool indexed,
            uint vertexCount,
            bool hasVertexBuffers) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectListNgg or GuestPrimitiveRectList
                    when AgcPrimitiveHelpers.ShouldDrawRectListAsTriangleStrip(
                        primitiveType,
                        indexed,
                        vertexCount,
                        hasVertexBuffers) => PrimitiveTopology.TriangleStrip,
                _ => PrimitiveTopology.TriangleList,
            };

        // Strip and fan topologies are the ones for which a restart index
        // splits primitives; list topologies never restart.
        private static bool RequiresPrimitiveRestart(PrimitiveTopology topology) =>
            topology is PrimitiveTopology.LineStrip
                or PrimitiveTopology.TriangleStrip
                or PrimitiveTopology.TriangleFan;

        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount)
        {
            var format = (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                // RDNA COLOR_2_10_10_10 stores component 0 (R) in bits
                // 0..9 and A in 30..31. Vulkan names that exact bit layout
                // A2B10G10R10_PACK32 (the packed name is MSB-to-LSB).
                (9, 0) => Format.A2B10G10R10UnormPack32,
                (9, 1) => Format.A2B10G10R10SNormPack32,
                (9, 2) => Format.A2B10G10R10UscaledPack32,
                (9, 3) => Format.A2B10G10R10SscaledPack32,
                (9, 4) => Format.A2B10G10R10UintPack32,
                (9, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                // Prospero VertexAttribFormat quirks also seen as buffer formats.
                (113, _) => Format.R32G32B32A32Sfloat,
                (121, _) => Format.R16G16Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

            return NarrowVkVertexFormat(format, componentCount);
        }

        /// <summary>
        /// Narrow a sharp's full VkFormat to the component count the VS fetch
        /// actually consumes.
        /// </summary>
        private static Format NarrowVkVertexFormat(Format format, uint usedComponents)
        {
            if (usedComponents == 0)
            {
                return format;
            }

            return (format, usedComponents) switch
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
                _ => format,
            };
        }

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            GuestIndexBuffer? indexBuffer,
            bool hasVertexBuffers) =>
            AgcPrimitiveHelpers.GetRectListDrawVertexCount(
                primitiveType,
                vertexCount,
                indexed: indexBuffer is not null,
                hasVertexBuffers);

        private static readonly bool _forceTitleVertexColorWhite =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_VERTEX_COLOR_WHITE") == "1";
    }
}
