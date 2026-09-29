// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly ulong _bufferParameterShader = ReadBufferParameterShader();
        private readonly int _bufferParameterSlot = int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_BUFFER_SLOT"), out var slot) ? slot : -1;
        private int _bufferParameterSamples;

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
