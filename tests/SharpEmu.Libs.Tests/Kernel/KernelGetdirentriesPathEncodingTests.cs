// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelGetdirentriesPathEncodingTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong GuestPathAddress = GuestMemoryBase + 0x100;
    private const ulong GuestEntriesAddress = GuestMemoryBase + 0x400;
    private const int OpenDirectoryFlag = 0x00020000;

    [Fact]
    public void GetdentsReturnsOriginalGuestNameForEncodedHostEntryOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mountPoint = $"/sharpemu_getdents_{Guid.NewGuid():N}";
        var mountRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-getdents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);

        var guestName = "Arcade Spirits: The New Challengers.dat";
        var hostName = HostFsPath.EncodeHostPathSegment(guestName);
        File.WriteAllText(Path.Combine(mountRoot, hostName), string.Empty);

        var memory = new FakeCpuMemory(GuestMemoryBase, 0x2000);
        var directoryFd = -1;

        try
        {
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            memory.WriteCString(GuestPathAddress, mountPoint);

            var openContext = new CpuContext(memory, Generation.Gen5);
            openContext[CpuRegister.Rdi] = GuestPathAddress;
            openContext[CpuRegister.Rsi] = OpenDirectoryFlag;
            Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(openContext));
            directoryFd = unchecked((int)openContext[CpuRegister.Rax]);

            var readContext = new CpuContext(memory, Generation.Gen5);
            readContext[CpuRegister.Rdi] = unchecked((ulong)directoryFd);
            readContext[CpuRegister.Rsi] = GuestEntriesAddress;
            readContext[CpuRegister.Rdx] = 512;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(readContext));

            var written = checked((int)readContext[CpuRegister.Rax]);
            Assert.InRange(written, 1, 512);
            var payload = new byte[written];
            Assert.True(memory.TryRead(GuestEntriesAddress, payload));

            var foundGuestName = false;
            for (var offset = 0; offset < payload.Length;)
            {
                var record = payload.AsSpan(offset);
                var recordLength = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
                var nameLength = (int)record[7];
                Assert.InRange(recordLength, 8, payload.Length - offset);
                Assert.InRange(nameLength, 0, recordLength - 8);

                var name = Encoding.UTF8.GetString(record.Slice(8, nameLength));
                if (name == guestName)
                {
                    Assert.Equal(8, record[6]);
                    foundGuestName = true;
                }

                offset += recordLength;
            }

            Assert.True(foundGuestName, "The directory listing should expose the original guest filename.");
        }
        finally
        {
            if (directoryFd >= 0)
            {
                var closeContext = new CpuContext(memory, Generation.Gen5);
                closeContext[CpuRegister.Rdi] = unchecked((ulong)directoryFd);
                KernelMemoryCompatExports.KernelClose(closeContext);
            }

            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            Directory.Delete(mountRoot, recursive: true);
        }
    }
}
