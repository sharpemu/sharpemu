// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(GuestRedZonePatcherCollection.Name)]
public sealed class GuestRedZoneCacheTests : IDisposable
{
    private const ulong SavedValue = 0x1122334455667788;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("sharpemu-redzone-cache-");
    private static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly DateTime OldStamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static byte[] RipLoadFunction() =>
    [
        0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
        0x48, 0x89, 0x44, 0x24, 0xF8,
        0x8B, 0x05, 0xEB, 0, 0, 0, // mov eax,[rip+0xeb], targeting image+0x100
        0x48, 0x33, 0x44, 0x24, 0xF8,
        0xC3,
    ];

    [Fact]
    public void CacheRebuildsRipRelativeTrampolineAtAnotherImageBase()
    {
        if (!Supported) return;
        var code = RipLoadFunction();
        using var first = new Image(code);
        var original = first.Patch(_directory.FullName, code);
        Assert.Equal(1, original.PatchedSites);
        Assert.Equal(SavedValue ^ 42, first.Execute());
        var path = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        File.SetLastWriteTimeUtc(path, OldStamp);

        using var second = new Image(code);
        Assert.NotEqual(first.Base, second.Base);
        second.WriteUInt32(0x100, 73);
        var cached = second.Patch(_directory.FullName, code);

        Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(original, cached);
        Assert.Equal(SavedValue ^ 73, second.Execute());
        Assert.Equal(0xE9, second.ReadByte(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedImageOrAppliedPatchInvalidatesPlan(bool changeImage)
    {
        if (!Supported) return;
        var code = RipLoadFunction();
        using var first = new Image(code);
        first.Patch(_directory.FullName, code, ["patch-a"]);
        var originalPath = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        File.SetLastWriteTimeUtc(originalPath, OldStamp);

        var changed = code.ToArray();
        if (changeImage) changed[2] ^= 1;
        using var second = new Image(changed);
        var result = second.Patch(_directory.FullName, changed, [changeImage ? "patch-a" : "patch-b"]);

        Assert.Equal(2, Directory.GetFiles(_directory.FullName, "*.bin").Length);
        Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(originalPath));
        Assert.Equal(1, result.PatchedSites);
        Assert.Equal((SavedValue ^ (changeImage ? 1UL : 0)) ^ 42, second.Execute());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("checksum")]
    [InlineData("oversized")]
    [InlineData("version")]
    [InlineData("key")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("instruction-boundary")]
    [InlineData("non-memory")]
    [InlineData("non-executable")]
    [InlineData("branch")]
    [InlineData("statistics")]
    [InlineData("sites")]
    public void InvalidCacheFallsBackToFullScan(string corruption)
    {
        if (!Supported) return;
        var code = RipLoadFunction();
        using (var first = new Image(code)) first.Patch(_directory.FullName, code);
        var path = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        var data = File.ReadAllBytes(path);
        if (corruption == "truncated") data = data[..10];
        else if (corruption == "checksum") data[0] ^= 1;
        else
        {
            var plan = JsonNode.Parse(data.AsSpan(32))!;
            switch (corruption)
            {
                case "version": plan["Version"] = 0; break;
                case "key": plan["Key"] = "another-image"; break;
                case "offset": plan["Sites"]![0]!["Offset"] = ulong.MaxValue; break;
                case "length": plan["Sites"]![0]!["Length"] = int.MaxValue; break;
                case "instruction-boundary": plan["Sites"]![0]!["Length"] = 5; break;
                case "non-memory": plan["Sites"]![0]!["Offset"] = 0; plan["Sites"]![0]!["Length"] = 10; break;
                case "non-executable": plan["Sites"]![0]!["Offset"] = 0x8000; break;
                case "branch": plan["Sites"]![0]!["TerminalBranch"] = true; break;
                case "statistics": plan["Scan"]!["ShaInstructionCount"] = -1; break;
                case "sites": plan["Sites"] = null; break;
            }
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan);
            data = [.. SHA256.HashData(payload), .. payload];
        }
        File.WriteAllBytes(path, data);
        if (corruption == "oversized")
        {
            using var oversized = File.OpenWrite(path);
            oversized.SetLength((32 << 20) + 1);
        }
        File.SetLastWriteTimeUtc(path, OldStamp);

        using var second = new Image(code);
        var result = second.Patch(_directory.FullName, code);

        Assert.NotEqual(OldStamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(1, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);
        Assert.Equal(0xE9, second.ReadByte(15));
        Assert.Equal(SavedValue ^ 42, second.Execute());
    }

    [Fact]
    public void UnavailableCacheDoesNotPreventPatching()
    {
        if (!Supported) return;
        var blockedDirectory = Path.Combine(_directory.FullName, "file");
        File.WriteAllText(blockedDirectory, "not a directory");
        var code = RipLoadFunction();
        using var image = new Image(code);

        Assert.Equal(1, image.Patch(blockedDirectory, code).PatchedSites);
        Assert.Equal(SavedValue ^ 42, image.Execute());
    }

    [Fact]
    public void EmptyPlanIsReused()
    {
        if (!Supported) return;
        byte[] code = [0x31, 0xC0, 0xC3];
        using (var first = new Image(code)) Assert.Equal(0, first.Patch(_directory.FullName, code).PatchedSites);
        var path = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        File.SetLastWriteTimeUtc(path, OldStamp);

        using var second = new Image(code);
        Assert.Equal(0, second.Patch(_directory.FullName, code).PatchedSites);
        Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(0UL, second.Execute());
    }

    [Theory]
    [InlineData(0u, 0UL)]
    [InlineData(1u, SavedValue)]
    public void CachedTerminalBranchPreservesItsTarget(uint value, ulong expected)
    {
        if (!Supported) return;
        byte[] code =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x83, 0x79, 0x40, 0x00, 0x74, 0x06,
            0x48, 0x8B, 0x44, 0x24, 0xF8, 0xC3, 0x31, 0xC0, 0xC3,
        ];
        using var first = new Image(code);
        Assert.Equal(1, first.Patch(_directory.FullName, code).PatchedSites);
        var path = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        File.SetLastWriteTimeUtc(path, OldStamp);
        using var second = new Image(code);
        second.WriteUInt32(0x40, value);

        Assert.Equal(1, second.Patch(_directory.FullName, code).PatchedSites);
        Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(expected, second.Execute());
    }

    [Fact]
    public void CachedEnclosingSpanPreservesTheShiftedCore()
    {
        if (!Supported) return;
        byte[] code =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x48, 0x8D, 0x91, 0x00, 0x20, 0x00, 0x00,
            0xB8, 0xFE, 0xFF, 0xFF, 0xFF, 0x21, 0x02,
            0x48, 0x8B, 0x44, 0x24, 0xF8, 0xC3, 0xEB, 0xF8,
        ];
        using var first = new Image(code);
        Assert.Equal(1, first.Patch(_directory.FullName, code).PatchedSites);
        var path = Assert.Single(Directory.GetFiles(_directory.FullName, "*.bin"));
        File.SetLastWriteTimeUtc(path, OldStamp);
        using var second = new Image(code);
        second.WriteUInt32(0x2000, 7);

        Assert.Equal(1, second.Patch(_directory.FullName, code).PatchedSites);
        Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(0xE9, second.ReadByte(22));
        Assert.Equal(SavedValue, second.Execute());
        Assert.Equal(6u, second.ReadUInt32(0x2000));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed unsafe class Image : IDisposable
    {
        private readonly PhysicalVirtualMemory _memory = new();
        private readonly ProgramHeader[] _headers;
        public ulong Base { get; }

        public Image(byte[] code)
        {
            const ulong holeSize = 0x4000000;
            var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            Assert.NotEqual(0UL, hole);
            Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
            Base = _memory.AllocateAt(hole, 0x10000);
            Assert.True(_memory.TryWrite(Base, code));
            WriteUInt32(0x100, 42);
            var ehFrame = new byte[32];
            ehFrame[0] = 1;
            ehFrame[2] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(ehFrame.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(ehFrame.AsSpan(16), Base);
            Assert.True(_memory.TryWrite(Base + 0x8000, ehFrame));
            _headers = [Header(ProgramHeaderType.Load, 0, (ulong)code.Length, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
                Header(ProgramHeaderType.GnuEhFrame, 0x8000, 32, ProgramHeaderFlags.Read)];
        }

        public GuestRedZonePatcher.PatchResult Patch(string directory, byte[] source, string[]? patchIds = null) =>
            GuestRedZonePatcher.Patch(_memory, _memory, _headers, Base, 0x10000, source, patchIds, directory);

        public byte ReadByte(int offset)
        {
            Span<byte> value = stackalloc byte[1];
            Assert.True(_memory.TryRead(Base + (ulong)offset, value));
            return value[0];
        }

        public void WriteUInt32(int offset, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Assert.True(_memory.TryWrite(Base + (ulong)offset, bytes));
        }

        public uint ReadUInt32(int offset)
        {
            Span<byte> bytes = stackalloc byte[4];
            Assert.True(_memory.TryRead(Base + (ulong)offset, bytes));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong Execute() => ((delegate* unmanaged<ulong, ulong>)Base)(Base);
        public void Dispose() => _memory.Dispose();

        private static ProgramHeader Header(ProgramHeaderType type, ulong offset, ulong size, ProgramHeaderFlags flags)
        {
            Span<byte> bytes = stackalloc byte[56];
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], offset);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], size);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], size);
            return MemoryMarshal.Read<ProgramHeader>(bytes);
        }
    }
}
