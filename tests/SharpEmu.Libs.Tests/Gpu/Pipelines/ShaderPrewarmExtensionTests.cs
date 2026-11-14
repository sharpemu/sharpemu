// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The prewarm list's side file: record kinds an earlier build cannot read (pixel programs, linked compile outcomes).
public sealed class ShaderPrewarmExtensionTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const int MainHeaderBytes = 3 * sizeof(uint);
    private const int ExtensionHeaderBytes = 2 * sizeof(uint);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    private string MainPath => Path.Combine(_directory, ShaderPrewarmList.FileName);

    private string ExtensionPath => Path.Combine(_directory, ShaderPrewarmList.ExtensionFileName);

    private static (ShaderCodeCapture Code, PixelPrewarmRecord Record) Pixel()
    {
        var code = new ShaderCodeCapture
        {
            Hash = 0x0BAD_F00D_1234_5678, CodeSize = 8, Address = CodeAddress, Generation = Generation.Gen5,
            Ranges = [new CodeRange(CodeAddress, [1, 2, 3, 4, 5, 6, 7, 8])],
        };
        var record = new PixelPrewarmRecord
        {
            Hash = code.Hash,
            CodeSize = code.CodeSize,
            Address = code.Address,
            UserDataBase = 0,
            UserDataCount = 4,
            PushDataCursor = 0,
            Info = new PixelInputInfo { InputCount = 1 },
            Outputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            InputEnable = 0x2,
            InputAddress = 0x2,
            Specialization = new ResourceSpecialization(),
        };
        return (code, record);
    }

    // A record as the list frames it: length, checksum, then the kind byte and the payload.
    private static byte[] Frame(byte kind, byte[] payload)
    {
        var body = new byte[1 + payload.Length];
        body[0] = kind;
        payload.CopyTo(body, 1);
        var record = new byte[sizeof(uint) + sizeof(ulong) + body.Length];
        BitConverter.TryWriteBytes(record.AsSpan(0, sizeof(uint)), (uint)body.Length);
        BitConverter.TryWriteBytes(record.AsSpan(sizeof(uint), sizeof(ulong)), XxHash3.HashToUInt64(body));
        body.CopyTo(record, sizeof(uint) + sizeof(ulong));
        return record;
    }

    [Fact]
    public void PixelRecordsGoToTheSideFile_SoAnEarlierBuildKeepsTheWholeMainList()
    {
        var (code, record) = Pixel();
        using (var list = Open())
        {
            list.RecordPixel(code, record);
        }

        // An earlier build reads the main list only; it holds nothing it would stop at and cut.
        Assert.Equal(MainHeaderBytes, new FileInfo(MainPath).Length);
        Assert.True(new FileInfo(ExtensionPath).Length > ExtensionHeaderBytes);
        using var reloaded = Open();
        var (loaded, loadedCode, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(ShaderPrewarmList.Identity(record), ShaderPrewarmList.Identity(loaded));
        Assert.Equal(code.Ranges[0].Bytes, Assert.Single(loadedCode.Ranges).Bytes);
    }

    // A side file of another version is not loaded: version 2 changed the image binding policy graphics records
    // translate under, and version 3 ends pixel and pre-rasterization records with their first use.
    [Fact]
    public void ASideFileOfAnotherVersionStartsEmpty()
    {
        var (code, record) = Pixel();
        using (var list = Open())
        {
            list.RecordPixel(code, record);
            list.RecordLinkedCompile(0x1234, slow: true, milliseconds: 90_000);
        }

        var bytes = File.ReadAllBytes(ExtensionPath);
        BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(uint), sizeof(uint)), 1u);
        File.WriteAllBytes(ExtensionPath, bytes);
        using (var reloaded = Open())
        {
            Assert.Empty(reloaded.LoadedGraphicsPrewarm());
            Assert.False(reloaded.IsSlowLinkedCompile(0x1234));
        }

        Assert.Equal(ExtensionHeaderBytes, new FileInfo(ExtensionPath).Length);
        Assert.Equal(3u, BitConverter.ToUInt32(File.ReadAllBytes(ExtensionPath), sizeof(uint)));
    }

    [Fact]
    public void LinkedCompileOutcomesPersist_AndTheLastOneWins()
    {
        const ulong slow = 0xE6D7_6D24_F59F_8015;
        const ulong other = 0xC4DF_2A00_067E_0666;
        using (var list = Open())
        {
            Assert.False(list.IsSlowLinkedCompile(slow));
            list.RecordLinkedCompile(slow, slow: true, 61_000);
            Assert.True(list.IsSlowLinkedCompile(slow));
        }

        var length = new FileInfo(ExtensionPath).Length;
        using (var list = Open())
        {
            Assert.True(list.IsSlowLinkedCompile(slow));
            // Nothing new to record: the same outcome again, and fast for a program never recorded slow.
            list.RecordLinkedCompile(slow, slow: true, 70_000);
            list.RecordLinkedCompile(other, slow: false, 3_000);
        }

        Assert.Equal(length, new FileInfo(ExtensionPath).Length);
        using (var list = Open())
        {
            Assert.False(list.IsSlowLinkedCompile(other));
            list.RecordLinkedCompile(slow, slow: false, 1_200);
            Assert.False(list.IsSlowLinkedCompile(slow));
        }

        using var reloaded = Open();
        Assert.False(reloaded.IsSlowLinkedCompile(slow));
    }

    [Fact]
    public void AnUnknownSideFileRecordIsSkippedAndKept()
    {
        const ulong first = 0x29BE_8047_C4E8_6AFE;
        const ulong second = 0xFC05_37B7_278D_B60C;
        using (var list = Open())
        {
            list.RecordLinkedCompile(first, slow: true, 90_000);
        }

        // A later build's record kind, then this build's own record after it.
        var unknown = Frame(0x7F, [9, 8, 7, 6, 5]);
        using (var file = new FileStream(ExtensionPath, FileMode.Append, FileAccess.Write))
        {
            file.Write(unknown);
        }

        var withUnknown = new FileInfo(ExtensionPath).Length;
        using (var list = Open())
        {
            Assert.True(list.IsSlowLinkedCompile(first));
            list.RecordLinkedCompile(second, slow: true, 120_000);
        }

        Assert.True(new FileInfo(ExtensionPath).Length > withUnknown);
        using (var reloaded = Open())
        {
            Assert.True(reloaded.IsSlowLinkedCompile(first));
            Assert.True(reloaded.IsSlowLinkedCompile(second));
        }

        var bytes = File.ReadAllBytes(ExtensionPath);
        Assert.True(bytes.AsSpan().IndexOf(unknown) > 0);
    }

    [Fact]
    public void ATornSideFileRecordEndsTheLoadAndIsCut()
    {
        const ulong first = 0x6573_0DAB_35E2_83F7;
        const ulong torn = 0x1111_2222_3333_4444;
        using (var list = Open())
        {
            list.RecordLinkedCompile(first, slow: true, 75_000);
        }

        var intact = new FileInfo(ExtensionPath).Length;
        using (var list = Open())
        {
            list.RecordLinkedCompile(torn, slow: true, 75_000);
        }

        using (var file = new FileStream(ExtensionPath, FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetLength(file.Length - 3);
        }

        using var reloaded = Open();
        Assert.True(reloaded.IsSlowLinkedCompile(first));
        Assert.False(reloaded.IsSlowLinkedCompile(torn));
        Assert.Equal(intact, new FileInfo(ExtensionPath).Length);
    }

    [Fact]
    public void TheSideFileKeepsItsOwnProgress()
    {
        using var list = Open();
        list.AppendProgress("stamp", new ulong[] { 1, 2 }, extension: true);
        list.AppendProgress("stamp", new ulong[] { 3 });

        // The compute prewarm clears its own progress only.
        list.ClearProgress();
        Assert.Empty(list.ReadProgress("stamp"));
        Assert.Equal(new HashSet<ulong> { 1, 2 }, list.ReadProgress("stamp", extension: true));
    }

    [Fact]
    public void PixelRecordsInTheMainFileOfAListWithoutSideFileStillLoad()
    {
        var (code, record) = Pixel();
        using (var list = Open())
        {
            list.RecordPixel(code, record);
        }

        // Move the side file's records to the end of the main list, where a list written without a side file keeps
        // them, and cut the first use from each pixel record.
        var extension = File.ReadAllBytes(ExtensionPath);
        using (var main = new FileStream(MainPath, FileMode.Append, FileAccess.Write))
        {
            main.Write(WithoutFirstUse(extension.AsSpan(ExtensionHeaderBytes)));
        }

        File.Delete(ExtensionPath);
        using var reloaded = Open();
        var (loaded, _, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(ShaderPrewarmList.Identity(record), ShaderPrewarmList.Identity(loaded));
    }

    // Records (length, checksum, kind and payload each) with the first use cut from every pixel record's payload.
    private static byte[] WithoutFirstUse(ReadOnlySpan<byte> records)
    {
        const byte pixelKind = 3;
        const int headerBytes = sizeof(uint) + sizeof(ulong);
        using var output = new MemoryStream();
        while (records.Length >= headerBytes)
        {
            var length = (int)BitConverter.ToUInt32(records);
            var body = records.Slice(headerBytes, length).ToArray();
            if (body[0] == pixelKind)
            {
                body = body[..^sizeof(float)];
            }

            output.Write(BitConverter.GetBytes((uint)body.Length));
            output.Write(BitConverter.GetBytes(XxHash3.HashToUInt64(body)));
            output.Write(body);
            records = records[(headerBytes + length)..];
        }

        return output.ToArray();
    }
}
