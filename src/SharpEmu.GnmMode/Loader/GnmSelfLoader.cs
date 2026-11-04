// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Patches;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.GnmMode.Loader;

public sealed partial class GnmSelfLoader : ISelfLoader
{
    private const string HideSplashScreenNid = "Vo5V8KAwCmk";
    private uint _nextTlsModule = 1;
    private uint _imageIndex;
    private IReadOnlyList<GamePatch> _patches = [];

    public SelfImage Load(ReadOnlySpan<byte> data, IVirtualMemory memory) => Load(data, memory, null, null);

    public SelfImage Load(ReadOnlySpan<byte> data, IVirtualMemory memory, IFileSystem? fs, string? root) =>
        LoadCore(data, memory, null, fs, root, null, true);

    public SelfImage Load(ReadOnlySpan<byte> data, IVirtualMemory memory, IModuleManager modules) =>
        Load(data, memory, modules, null, null);

    public SelfImage Load(ReadOnlySpan<byte> data, IVirtualMemory memory, IModuleManager modules,
        IFileSystem? fs, string? root, string? imageName = null) =>
        LoadCore(data, memory, modules, fs, root, imageName, true);

    public SelfImage LoadAdditional(ReadOnlySpan<byte> data, IVirtualMemory memory, IModuleManager modules,
        IFileSystem? fs, string? root, string? imageName = null) =>
        LoadCore(data, memory, modules, fs, root, imageName, false);

    private SelfImage LoadCore(ReadOnlySpan<byte> data, IVirtualMemory memory, IModuleManager? modules,
        IFileSystem? fs, string? root, string? imageName, bool main)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var layout = GnmLoader.Open(data);
        var headers = layout.ProgramHeaders;
        var info = main ? ReadApplicationInfo(fs, root) : default;
        if (main)
        {
            Shaders.GcnShaderFrontend.ResetVertexFetchTargets();
            KernelMemoryCompatExports.ResetBackingMappings(memory as IGuestBackedSpace);
            memory.Clear();
            SharpEmu.Libs.Agc.AgcExports.ResetTessellationFactorRing(memory);
            SharpEmu.Libs.Agc.AgcExports.SetShaderFrontend(memory, new Shaders.GcnShaderFrontend(memory));
            Gpu.GnmCommandPackets.Reset(memory);
            Libraries.Gnm.GnmDriverExports.ResetComputeMappings(memory);
            Libraries.Gnm.GnmDriverExports.ResetEmbeddedShaders(memory);
            Libraries.Np.GnmMatching2Exports.Reset();
            Libraries.Np.GnmNpTusExports.Reset();
            Libraries.SaveData.GnmSaveDataDialogExports.Reset(memory);
            Libraries.Codec.GnmVideodec2Exports.Reset(memory);
            GuestTlsTemplate.Reset();
            _nextTlsModule = 1;
            _imageIndex = 0;
            _patches = new GamePatchRepository(GamePatchRepository.DefaultDirectory)
                .LoadActive(info.TitleId, info.Version, message => Console.Error.WriteLine($"[GNM][PATCH] {message}"));
        }

        var imageSize = headers.Where(IsLoad).Select(header => checked(header.VirtualAddress + header.MemorySize))
            .DefaultIfEmpty().Max();
        var imageBase = AllocateImage(memory, imageSize, main);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (!IsLoad(header) || header.MemorySize == 0)
                continue;
            if (header.FileSize > header.MemorySize)
                throw new InvalidDataException("PS4 segment file size exceeds its memory size.");
            var source = header.FileSize == 0 ? ReadOnlySpan<byte>.Empty : layout.ReadSegment(data, i);
            memory.Map(checked(imageBase + header.VirtualAddress), header.MemorySize,
                header.FileSize == 0 ? 0 : layout.GetSegmentOffset(i), source, header.Flags);
        }

        var tls = headers.FirstOrDefault(header => header.HeaderType == ProgramHeaderType.Tls);
        var tlsId = tls.MemorySize == 0 ? 0 : _nextTlsModule++;
        var tlsOffset = tlsId == 0 ? 0 : GuestTlsTemplate.RegisterModule(tlsId,
            ReadTlsImage(memory, imageBase, tls), tls.MemorySize, tls.Alignment, tls.VirtualAddress);

        var strings = ReadTable(layout, data, 0x61000035, 0x61000037);
        var symbols = ReadSymbols(ReadTable(layout, data, 0x61000039, 0x6100003F));
        var relocations = ReadRelocations(layout, data);
        var stubs = CreateStubs(memory, modules, relocations, symbols, strings);
        if (main)
        {
            SharpEmu.Libs.VideoOut.VideoOutExports.SplashHoldsGuestFrames = stubs.ContainsValue(HideSplashScreenNid);
            SharpEmu.Libs.VideoOut.VideoOutExports.CompatibilityMode = true;
            SharpEmu.Libs.Gpu.Buffers.GuestBufferCache.PublishCopySourceImages = true;
        }
        var imported = ApplyRelocations(memory, imageBase, tlsId, relocations, symbols, strings, stubs);
        var runtimeSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Section == 0 || symbol.Name == 0 || (symbol.Info & 15) == 6)
                continue;
            var name = ReadString(strings, symbol.Name);
            var address = SymbolAddress(symbol, imageBase);
            runtimeSymbols.TryAdd(name, address);
            runtimeSymbols.TryAdd(Nid(name), address);
        }

        var importedModules = layout.ImportedModuleNames.Select(offset => ReadString(strings, offset))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var executable = imageName ?? (main ? "eboot.bin" : string.Empty);
        var appliedPatchIds = new List<string>();
        foreach (var patch in _patches.Where(patch => string.Equals(patch.Executable, executable, StringComparison.OrdinalIgnoreCase)))
        {
            if (!patch.TryApply(memory, headers, imageBase, out var error))
                Console.Error.WriteLine($"[GNM][PATCH] Could not apply '{patch.Name}': {error}");
            else
                appliedPatchIds.Add(patch.Id);
        }
        if (tlsId != 0)
            GuestTlsTemplate.UpdateInitializationImage(tlsId, ReadTlsImage(memory, imageBase, tls));

        // A PS4 PRX can put DT_INIT at virtual offset zero; absence is a missing tag, not a zero value.
        var initEntry = layout.DynamicEntries.TryGetValue(12, out var init) ? checked(imageBase + init) : 0;
        var preInitializers = ReadInitializerArray(layout, memory, imageBase, 32, 33);
        var initializers = ReadInitializerArray(layout, memory, imageBase, 25, 27);
        if (initEntry != 0)
            initializers.Insert(0, initEntry);
        if (memory is PhysicalVirtualMemory physical)
            _ = GuestRedZonePatcher.Patch(memory, physical, headers, imageBase, imageSize, data, appliedPatchIds);
        var proc = headers.FirstOrDefault(header => header.HeaderType == ProgramHeaderType.SceProcParam);

        Console.Error.WriteLine($"[GNM][LOADER] {executable}: base=0x{imageBase:X} entry=0x{imageBase + layout.ElfHeader.EntryPoint:X} " +
            $"relocations={relocations.Count} imports={stubs.Count} exports={runtimeSymbols.Count} tls={tlsId}");
        return new SelfImage(layout.IsSelf, layout.ElfHeader, headers, memory.SnapshotRegions(), stubs,
            runtimeSymbols, imported, preInitializers, initializers, initEntry, imageBase,
            proc.MemorySize == 0 ? 0 : checked(imageBase + proc.VirtualAddress), info.Title, info.TitleId, info.Version,
            tlsId, tls.MemorySize, tlsOffset, importedModules);
    }

    private static bool IsLoad(ProgramHeader header) =>
        header.HeaderType is ProgramHeaderType.Load or ProgramHeaderType.SceRelro;

    private static ulong AllocateImage(IVirtualMemory memory, ulong size, bool main)
    {
        var address = main ? 0x400000UL : Math.Max(0x2000000UL,
            memory.SnapshotRegions().Where(region => region.VirtualAddress < 0x40000000)
                .Select(region => Align(checked(region.VirtualAddress + region.MemorySize + 0x200000))).DefaultIfEmpty().Max());
        if (memory is not PhysicalVirtualMemory physical || size == 0)
            return address;

        if (main)
        {
            if (physical.TryAllocateAtExact(address, size, true, out var allocated))
                return allocated;

            // The Windows host can occupy 0x400000 before the PS4 image loads.
            // Keep the image below 2 GiB for relocations with 32-bit targets.
            foreach (var candidate in physical.EnumerateFreeHostRanges(0x600000, 0x80000000, size, 0x200000))
            {
                if (!physical.TryAllocateAtExact(candidate, size, true, out allocated))
                    continue;
                Console.Error.WriteLine($"[GNM][LOADER] Main image relocated to free host address 0x{allocated:X}.");
                return allocated;
            }
        }
        else
        {
            for (var attempt = 0; attempt < 256; attempt++, address += 0x200000)
            {
                if (address >= 0x40000000 || size > 0x40000000 - address)
                    break;
                if (physical.TryAllocateAtExact(address, size, true, out var allocated))
                    return allocated;
            }

            foreach (var candidate in physical.EnumerateFreeHostRanges(0x2000000, 0x80000000, size, 0x200000))
            {
                if (!physical.TryAllocateAtExact(candidate, size, true, out var allocated))
                    continue;
                Console.Error.WriteLine($"[GNM][LOADER] Module image relocated to free host address 0x{allocated:X}.");
                return allocated;
            }
        }
        throw new InvalidOperationException($"Cannot allocate PS4 image: address=0x{address:X} size=0x{size:X}.");
    }

    private static byte[] ReadTlsImage(IVirtualMemory memory, ulong imageBase, ProgramHeader tls)
    {
        if (tls.FileSize > tls.MemorySize)
            throw new InvalidDataException("PS4 TLS file size exceeds its memory size.");
        return ReadMemory(memory, checked(imageBase + tls.VirtualAddress), tls.FileSize);
    }

    private static byte[] ReadMemory(IVirtualMemory memory, ulong address, ulong size)
    {
        var result = new byte[checked((int)size)];
        if (result.Length != 0 && !memory.TryRead(address, result))
            throw new InvalidDataException($"PS4 image references unmapped memory at 0x{address:X}.");
        return result;
    }

    private static byte[] ReadTable(GnmLoader layout, ReadOnlySpan<byte> data, long tag, long sizeTag)
    {
        if (!layout.DynamicEntries.TryGetValue(sizeTag, out var size) || size == 0)
            return [];
        if (!layout.TryReadDynamicTable(data, tag, size, out var table))
            throw new InvalidDataException($"PS4 dynamic table 0x{tag:X} has no backing segment.");
        return table;
    }

    private static List<ulong> ReadInitializerArray(GnmLoader layout, IVirtualMemory memory, ulong imageBase, long tag, long sizeTag)
    {
        var size = layout.DynamicEntries.GetValueOrDefault(sizeTag);
        if (size % 8 != 0)
            throw new InvalidDataException("PS4 initializer array has an invalid size.");
        var result = new List<ulong>();
        if (size == 0)
            return result;
        var bytes = ReadMemory(memory, checked(imageBase + layout.DynamicEntries[tag]), size);
        for (var i = 0; i < bytes.Length; i += 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i));
            if (value is not (0 or ulong.MaxValue) && !result.Contains(value))
                result.Add(value);
        }
        return result;
    }

    private static (string? Title, string? TitleId, string? Version) ReadApplicationInfo(IFileSystem? fs, string? root)
    {
        var path = string.IsNullOrEmpty(root) ? "sce_sys/param.sfo" : $"{root}/sce_sys/param.sfo";
        Libraries.AppContent.GnmAppContentExports.SetUserDefinedParams(new int[GnmParamSfo.UserDefinedParamCount]);
        Libraries.Kernel.GnmKernelExports.SetCpuMode(0);
        if (fs?.TryReadAllBytes(path, out var bytes) != true)
            return default;

        var info = GnmParamSfo.Read(bytes);
        Libraries.AppContent.GnmAppContentExports.SetUserDefinedParams(GnmParamSfo.ReadUserDefinedParams(bytes));
        Libraries.Kernel.GnmKernelExports.SetCpuMode(GnmParamSfo.ReadCpuMode(bytes));
        Console.WriteLine($"[LOADER] Loading param.sfo at {path}");
        Console.WriteLine($"[LOADER] Title: {info.Title ?? "(unknown)"}, " +
            $"TitleId: {info.TitleId ?? "(unknown)"}, " +
            $"Version: {info.Version ?? "(unknown)"}");
        return info;
    }

    private static string ReadString(ReadOnlySpan<byte> table, uint offset)
    {
        if (offset >= table.Length)
            throw new InvalidDataException("PS4 symbol string offset exceeds its table.");
        var bytes = table[(int)offset..];
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
            throw new InvalidDataException("Unterminated PS4 symbol string.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static string Nid(string name) => name.Split('#', 2)[0];
    private static ulong Align(ulong value) => checked(value + 0xFFF) & ~0xFFFUL;
}
