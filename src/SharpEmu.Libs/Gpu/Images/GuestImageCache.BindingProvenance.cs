// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

// Earlier texture bindings at an address, compared with the guest bytes of a later volume upload.
public sealed partial class GuestImageCache
{
    private const int MaximumProvenanceAddresses = 4096;
    private const int MaximumProvenancePerAddress = 4;
    private const int MaximumProvenanceReports = 128;
    internal const int ProvenanceHashBytes = 64 * 1024;

    private readonly record struct BindingProvenance(
        ulong ShaderHash, int ImageIndex, string Words, string Format, string Extent,
        long UnmapGeneration, ulong ByteHash, int HashedBytes, ulong Tick);

    private readonly object _bindingProvenanceGate = new();
    private readonly Dictionary<ulong, List<BindingProvenance>> _bindingProvenance = new();
    private readonly HashSet<ulong> _reportedProvenance = new();
    private long _unmapGeneration;

    // Keeps the first binding of each description at an address, with a hash of its guest bytes.
    public void TraceTextureBinding(ulong shaderHash, int imageIndex, ReadOnlySpan<uint> words, in ImageRequest request)
    {
        if (!ImageClearTrace.Enabled) return;
        ref readonly var description = ref request.Description;
        var address = description.Data.Address;
        var format = description.PixelFormat.ToString();
        var extent = $"{description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth}";
        lock (_bindingProvenanceGate)
        {
            if (!_bindingProvenance.TryGetValue(address, out var entries))
            {
                if (_bindingProvenance.Count >= MaximumProvenanceAddresses) return;
                _bindingProvenance[address] = entries = new List<BindingProvenance>(1);
            }
            if (entries.Count >= MaximumProvenancePerAddress ||
                entries.Exists(entry => entry.Format == format && entry.Extent == extent)) return;
            var hashed = (int)Math.Min(description.Data.Size, ProvenanceHashBytes);
            var byteHash = TryHashGuestBytes(address, hashed, out var hash) ? hash : 0;
            entries.Add(new BindingProvenance(shaderHash, imageIndex, string.Join(',', words.ToArray().Select(word => word.ToString("X8"))),
                format, extent, Interlocked.Read(ref _unmapGeneration), byteHash, hashed, _scheduler.CurrentTick));
        }
    }

    // Compare sampled backing bytes. Equal hashes do not establish image identity or GPU ownership.
    private void ReportVolumeProvenance(CachedImage image)
    {
        ref readonly var description = ref image.Description;
        var address = description.Data.Address;
        List<BindingProvenance>? entries;
        lock (_bindingProvenanceGate)
        {
            if (_reportedProvenance.Count >= MaximumProvenanceReports || !_reportedProvenance.Add(address) ||
                !_bindingProvenance.TryGetValue(address, out entries)) return;
            entries = [.. entries];
        }
        var generation = Interlocked.Read(ref _unmapGeneration);
        foreach (var entry in entries)
        {
            var current = TryHashGuestBytes(address, entry.HashedBytes, out var hash) ? $"0x{hash:X16}" : "unreadable";
            Console.Error.WriteLine($"[GPU][TRACE] BindingProvenance image=0x{address:X16} volume={description.PixelFormat} " +
                $"{description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} earlierFormat={entry.Format} " +
                $"earlierExtent={entry.Extent} shader=0x{entry.ShaderHash:X16} image={entry.ImageIndex} tick={entry.Tick} " +
                $"words={entry.Words} hashedBytes=0x{entry.HashedBytes:X} earlierHash=0x{entry.ByteHash:X16} currentHash={current} " +
                $"match={current == $"0x{entry.ByteHash:X16}"} unmapsBetween={generation - entry.UnmapGeneration}");
        }
    }

    private bool TryHashGuestBytes(ulong address, int length, out ulong hash)
    {
        hash = 0;
        if (length <= 0) return false;
        var bytes = new byte[length];
        if (!_backing.TryReadBacking(address, bytes)) return false;
        hash = HashBytes(bytes);
        return true;
    }

    // FNV-1a: a stable identity for equal byte ranges, not a cryptographic digest.
    internal static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
