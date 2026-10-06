// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly ulong _bufferParameterShader = ReadBufferParameterShader();
        private readonly int _bufferParameterSlot = int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_BUFFER_SLOT"), out var slot) ? slot : -1;
        private int _bufferParameterSamples;
        private int _vertexParameterSamples;

        public void TraceVertexBuffer(ulong shaderHash, VertexAttributeResource attribute, VertexInputBuffer input,
            BufferBinding binding, ulong requestedBytes, ulong acquiredBytes)
        {
            if (_bufferParameterShader == 0 || shaderHash != _bufferParameterShader ||
                attribute.AttributeId != _bufferParameterSlot || _vertexParameterSamples >= 64) return;

            var descriptor = attribute.Descriptor;
            Span<byte> bytes = stackalloc byte[16];
            var readable = _guestBacking.TryReadBacking(descriptor.Address, bytes);
            var stream = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream);
            var offset = binding.Offset + attribute.OffsetBytes;
            var mappedBytes = "unavailable";
            if (binding.Handle == stream.Handle.Handle && offset <= stream.Size && stream.Size - offset >= 16)
                mappedBytes = Convert.ToHexString(stream.Mapped.Slice(checked((int)offset), 16));

            Console.Error.WriteLine($"[GPU][TRACE] VertexBufferParameter sample={++_vertexParameterSamples} " +
                $"tick={_scheduler.CurrentTick} hash=0x{shaderHash:X16} attribute={attribute.AttributeId} slot={attribute.BufferIndex} " +
                $"descriptor=[{descriptor.Word0:X8},{descriptor.Word1:X8},{descriptor.Word2:X8},{descriptor.Word3:X8}] " +
                $"address=0x{descriptor.Address:X16} stride={descriptor.Stride} records={descriptor.RecordCount} " +
                $"format={descriptor.Format} oob={descriptor.OutOfBounds} components={attribute.RegisterCount} " +
                $"descriptorBytes={input.Size} requestedBytes={requestedBytes} acquiredBytes={acquiredBytes} attributeOffset={attribute.OffsetBytes} " +
                $"buffer=0x{binding.Handle:X16} offset={offset} guestBytes={(readable ? Convert.ToHexString(bytes) : "unavailable")} " +
                $"streamBytes={mappedBytes}");
        }

        private static ulong ReadBufferParameterShader()
        {
            var text = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_BUFFER_SHADER")?.Trim();
            if (text is null) return 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hash) ? hash : 0;
        }

        private void TraceBufferParameter(ulong shaderHash, int slot, ulong address, ulong size,
            ulong bufferHandle, ulong bufferOffset, bool written)
        {
            if (_bufferParameterShader == 0 || shaderHash != _bufferParameterShader ||
                slot != _bufferParameterSlot || _bufferParameterSamples >= 16384) return;

            Span<byte> bytes = stackalloc byte[(int)Math.Min(size, 128UL)];
            // Read the backing without downloading GPU data or changing cache ownership.
            var readable = _guestBacking.TryReadBacking(address, bytes);
            var sample = ++_bufferParameterSamples;
            Console.Error.WriteLine($"[GPU][TRACE] BufferParameter time={DateTime.UtcNow:O} sample={sample} " +
                $"tick={_scheduler.CurrentTick} hash=0x{shaderHash:X16} slot={slot} address=0x{address:X16} " +
                $"size={size} buffer=0x{bufferHandle:X16} offset={bufferOffset} written={written} " +
                $"readable={readable} bytes={(readable ? Convert.ToHexString(bytes) : "unavailable")}");
            if (sample == 16384)
                Console.Error.WriteLine("[GPU][TRACE] BufferParameter sample limit reached; tracing stopped.");
        }
    }
}
