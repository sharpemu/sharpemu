// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.Images;

// SHARPEMU_TRACE_IMAGE_DROPS=1 reports every time an image loses GPU-written contents that
// never reached guest memory: the code site, the image, how much of it the cause covered and
// the guest render pass (from its push markers). Each surface reports its first occurrences
// and then every power of two, so a surface dropped every frame stays visible but bounded.
internal static class ImageDropTrace
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_IMAGE_DROPS") == "1";

    private static readonly Dictionary<(string Site, ulong Address, uint Format, uint Width, uint Height), long> _counts = new();
    private static readonly object _gate = new();

    // The guest pass that is recording, as nested push-marker names.
    public static string Pass { get; private set; } = "";

    // The byte range whose GPU write invalidates images, while the invalidation runs.
    [ThreadStatic] public static ulong CauseAddress;
    [ThreadStatic] public static ulong CauseSize;

    public static void Record(CachedImage image, string file, int line)
    {
        ref readonly var description = ref image.Description;
        var site = $"{Path.GetFileNameWithoutExtension(file)}:{line}";
        var key = (site, description.Data.Address, (uint)description.PixelFormat, description.Extent.Width, description.Extent.Height);
        long count;
        lock (_gate)
        {
            _counts.TryGetValue(key, out count);
            _counts[key] = ++count;
        }

        if (count > 3 && (count & (count - 1)) != 0)
        {
            return;
        }

        var coverage = "";
        if (CauseSize != 0)
        {
            var begin = Math.Max(CauseAddress, description.Data.Address);
            var end = Math.Min(CauseAddress + CauseSize, description.Data.Address + description.Data.Size);
            var covered = end > begin ? end - begin : 0;
            coverage = FormattableString.Invariant(
                $" cause=0x{CauseAddress:X}+0x{CauseSize:X} covers={(double)covered / Math.Max(1UL, description.Data.Size):P1}");
        }

        Console.Error.WriteLine(
            $"[IMAGE][DROP] site={site} n={count} image=0x{description.Data.Address:X} bytes=0x{description.Data.Size:X} " +
            $"{description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} format={description.PixelFormat} " +
            $"guest={(uint)description.GuestFormat} tile={(uint)description.TileMode} levels={description.Resources.Levels} " +
            $"layers={description.Resources.Layers} target={image.Uses.RenderTarget} depth={image.Uses.DepthTarget} " +
            $"bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty} " +
            $"time={DateTime.Now:HH:mm:ss}{coverage} pass='{Pass}'");
    }

    // One marker stack per command queue: graphics and compute rings interleave their slices.
    private static readonly Dictionary<object, List<string>> _markers = new(ReferenceEqualityComparer.Instance);

    public static void PushMarker(object queue, ReadOnlySpan<uint> payload)
    {
        var bytes = MemoryMarshal.AsBytes(payload);
        var end = bytes.IndexOf((byte)0);
        var name = System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
        lock (_gate)
        {
            if (!_markers.TryGetValue(queue, out var stack))
            {
                _markers[queue] = stack = new List<string>();
            }

            // A stack that never pops is not a nesting; keep it bounded.
            if (stack.Count >= 8)
            {
                stack.RemoveAt(0);
            }

            stack.Add(name);
            Pass = string.Join('/', stack);
        }
    }

    public static void PopMarker(object queue)
    {
        lock (_gate)
        {
            if (_markers.TryGetValue(queue, out var stack) && stack.Count != 0)
            {
                stack.RemoveAt(stack.Count - 1);
                Pass = string.Join('/', stack);
            }
        }
    }
}
