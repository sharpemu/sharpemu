// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// A guest filename that Windows cannot hold used to reach CreateFile unchanged.
// Windows does not fail those: "save: 0.dat" writes the alternate data stream
// ": 0.dat" on a file named "save", so the title's write succeeds, the file is
// not there on the next boot, and the reopen faults. These drive the real
// open/getdents syscalls end to end, so they assert the property that actually
// matters — whatever name the guest writes under, it can read back and
// enumerate — on every host rather than pinning a Windows-only spelling.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelGuestFilenameEncodingTests : IDisposable
{
    private const int O_RDONLY = 0x0;
    private const int O_WRONLY = 0x1;
    private const int O_CREAT = 0x0200;
    private const int O_DIRECTORY = 0x00020000;

    private const string GuestMount = "/sharpemu_encoding_mnt";
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong BufferAddress = MemoryBase + 0x800;

    private readonly string _tempRoot;
    private readonly string _mountRoot;

    public KernelGuestFilenameEncodingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-fsname-{Guid.NewGuid():N}");
        _mountRoot = Path.Combine(_tempRoot, "mnt");
        Directory.CreateDirectory(_mountRoot);
        KernelMemoryCompatExports.RegisterGuestPathMount(GuestMount, _mountRoot);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.UnregisterGuestPathMount(GuestMount);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    // The name from the Arcade Spirits report, and the rest of the characters
    // Windows rejects or rewrites.
    [InlineData("rg_ac_Arcade Spirits: The New Challengers_0.dat")]
    [InlineData("progress?.sav")]
    [InlineData("chapter*final.dat")]
    [InlineData("say \"hello\".txt")]
    [InlineData("a<b>c|d.bin")]
    [InlineData("trailing dot.")]
    [InlineData("nul.dat")]
    // Names that are already legal must keep working untouched.
    [InlineData("ordinary.sav")]
    [InlineData("100% Complete.sav")]
    public void GuestWrittenFileIsReadableAndEnumerableUnderItsOwnName(string guestName)
    {
        var guestPath = $"{GuestMount}/{guestName}";
        var payload = "slot"u8.ToArray();

        var writeFd = Open(guestPath, O_WRONLY | O_CREAT);
        Assert.True(writeFd > 0, $"open for write failed with {writeFd}");
        Assert.Equal(payload.Length, Write(writeFd, payload));
        Close(writeFd);

        // The name the guest used must resolve back to the same bytes. Before the
        // encoding this failed on Windows: the data landed in an alternate data
        // stream and the reopen found a truncated, empty file.
        var readFd = Open(guestPath, O_RDONLY);
        Assert.True(readFd > 0, $"reopen failed with {readFd}");
        Assert.Equal(payload, Read(readFd, payload.Length));
        Close(readFd);

        // ...and a directory listing must hand that same name back, since the
        // guest turns straight around and opens what it read.
        Assert.Contains(guestName, ReadDirectory(GuestMount));
    }

    [Fact]
    public void EncodedSubdirectoryIsReportedAsADirectory()
    {
        // The dirent type byte is derived by re-joining the entry name to the
        // host path, so an entry handed back in guest form has to be re-encoded
        // before it is probed; otherwise every encoded directory reports as a
        // regular file (DT_REG) and the guest never descends into it.
        const string guestName = "slot: 1";
        Assert.True(Mkdir($"{GuestMount}/{guestName}"), "mkdir failed");

        var entries = ReadDirectoryEntries(GuestMount);
        var entry = Assert.Single(entries, e => e.Name == guestName);
        Assert.Equal(4, entry.Type); // DT_DIR

        // A file inside it is reachable through the guest name of both segments.
        var nestedFd = Open($"{GuestMount}/{guestName}/data:0.bin", O_WRONLY | O_CREAT);
        Assert.True(nestedFd > 0, $"nested open failed with {nestedFd}");
        Close(nestedFd);
        Assert.Contains("data:0.bin", ReadDirectory($"{GuestMount}/{guestName}"));
    }

    [Fact]
    public void DistinctGuestNamesDoNotShareAHostFile()
    {
        // Replacing invalid characters with '_' would alias these two saves onto
        // one host file, quietly destroying the first.
        var colonFd = Open($"{GuestMount}/slot:0.sav", O_WRONLY | O_CREAT);
        Assert.True(colonFd > 0);
        Assert.Equal(5, Write(colonFd, "colon"u8.ToArray()));
        Close(colonFd);

        var underscoreFd = Open($"{GuestMount}/slot_0.sav", O_WRONLY | O_CREAT);
        Assert.True(underscoreFd > 0);
        Assert.Equal(5, Write(underscoreFd, "under"u8.ToArray()));
        Close(underscoreFd);

        Assert.Equal("colon"u8.ToArray(), ReadAll($"{GuestMount}/slot:0.sav"));
        Assert.Equal("under"u8.ToArray(), ReadAll($"{GuestMount}/slot_0.sav"));
        Assert.Equal(2, Directory.GetFiles(_mountRoot).Length);
    }

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Open(string guestPath, int flags)
    {
        var context = NewContext(out var memory);
        memory.WriteCString(PathAddress, guestPath);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)(long)flags);
        context[CpuRegister.Rdx] = 0x1B6; // 0666
        var result = KernelMemoryCompatExports.KernelOpenUnderscore(context);
        return result < 0 ? result : unchecked((int)context[CpuRegister.Rax]);
    }

    private static void Close(int fd)
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = unchecked((ulong)(long)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(context));
    }

    private static int Write(int fd, byte[] payload)
    {
        var context = NewContext(out var memory);
        Assert.True(memory.TryWrite(BufferAddress, payload));
        context[CpuRegister.Rdi] = unchecked((ulong)(long)fd);
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = (ulong)payload.Length;
        Assert.Equal(0, KernelMemoryCompatExports.KernelWrite(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static byte[] Read(int fd, int length)
    {
        var context = NewContext(out var memory);
        context[CpuRegister.Rdi] = unchecked((ulong)(long)fd);
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = (ulong)length;
        Assert.Equal(0, KernelMemoryCompatExports.KernelRead(context));

        var read = unchecked((int)context[CpuRegister.Rax]);
        var buffer = new byte[read];
        Assert.True(memory.TryRead(BufferAddress, buffer));
        return buffer;
    }

    private static byte[] ReadAll(string guestPath)
    {
        var fd = Open(guestPath, O_RDONLY);
        Assert.True(fd > 0, $"open failed with {fd}");
        var bytes = Read(fd, 64);
        Close(fd);
        return bytes;
    }

    private static bool Mkdir(string guestPath)
    {
        var context = NewContext(out var memory);
        memory.WriteCString(PathAddress, guestPath);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = 0x1FF; // 0777
        return KernelMemoryCompatExports.KernelMkdir(context) == 0;
    }

    private static List<string> ReadDirectory(string guestPath) =>
        ReadDirectoryEntries(guestPath).ConvertAll(static entry => entry.Name);

    // Drains getdents, which yields one 512-byte dirent per call: u32 fileno,
    // u16 reclen, u8 type, u8 namlen, then the name.
    private static List<(string Name, byte Type)> ReadDirectoryEntries(string guestPath)
    {
        var fd = Open(guestPath, O_RDONLY | O_DIRECTORY);
        Assert.True(fd > 0, $"opendir failed with {fd}");

        var entries = new List<(string, byte)>();
        try
        {
            for (var guard = 0; guard < 256; guard++)
            {
                var context = NewContext(out var memory);
                context[CpuRegister.Rdi] = unchecked((ulong)(long)fd);
                context[CpuRegister.Rsi] = BufferAddress;
                context[CpuRegister.Rdx] = 512;
                Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(context));
                if (context[CpuRegister.Rax] == 0)
                {
                    return entries;
                }

                var dirent = new byte[512];
                Assert.True(memory.TryRead(BufferAddress, dirent));
                var type = dirent[6];
                var nameLength = dirent[7];
                var name = Encoding.UTF8.GetString(dirent, 8, nameLength);
                Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(dirent.AsSpan(4, 2)));
                if (name is not ("." or ".."))
                {
                    entries.Add((name, type));
                }
            }

            throw new InvalidOperationException("getdents never reported end of directory");
        }
        finally
        {
            Close(fd);
        }
    }
}
