// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelFileRenameTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SourcePathAddress = MemoryBase + 0x100;
    private const ulong DestinationPathAddress = MemoryBase + 0x300;
    private const ulong ReadBufferAddress = MemoryBase + 0x500;

    [Fact]
    public void KernelRename_ReplacesDestinationWhileSourceDescriptorRemainsOpen()
    {
        var identity = Guid.NewGuid().ToString("N");
        var mountPoint = $"/rename-{identity}";
        var hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-rename-{identity}");
        var sourceHostPath = Path.Combine(hostRoot, "STEMP000.DAT");
        var destinationHostPath = Path.Combine(hostRoot, "SDATA000.DAT");
        var sourceGuestPath = $"{mountPoint}/STEMP000.DAT";
        var destinationGuestPath = $"{mountPoint}/SDATA000.DAT";
        byte[] payload = [0x10, 0x20, 0x30, 0x40];
        var fileDescriptor = -1;

        Directory.CreateDirectory(hostRoot);
        File.WriteAllBytes(sourceHostPath, payload);
        File.WriteAllBytes(destinationHostPath, [0xFF]);
        KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, hostRoot);

        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(SourcePathAddress, sourceGuestPath);
        memory.WriteCString(DestinationPathAddress, destinationGuestPath);

        try
        {
            context[CpuRegister.Rdi] = SourcePathAddress;
            context[CpuRegister.Rsi] = 0x2;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExports.KernelOpen(context));
            fileDescriptor = unchecked((int)context[CpuRegister.Rax]);
            Assert.True(fileDescriptor >= 3);

            context[CpuRegister.Rdi] = SourcePathAddress;
            context[CpuRegister.Rsi] = DestinationPathAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelRename(context));
            Assert.False(File.Exists(sourceHostPath));
            var renamedPayload = new byte[payload.Length];
            using (var destinationStream = new FileStream(
                destinationHostPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                destinationStream.ReadExactly(renamedPayload);
            }
            Assert.Equal(payload, renamedPayload);

            context[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
            context[CpuRegister.Rsi] = ReadBufferAddress;
            context[CpuRegister.Rdx] = unchecked((uint)payload.Length);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelReadUnderscore(context));
            Assert.Equal((ulong)payload.Length, context[CpuRegister.Rax]);

            Span<byte> actual = stackalloc byte[payload.Length];
            Assert.True(memory.TryRead(ReadBufferAddress, actual));
            Assert.True(actual.SequenceEqual(payload));

            context[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelClose(context));
            fileDescriptor = -1;
            Assert.Equal(payload, File.ReadAllBytes(destinationHostPath));
        }
        finally
        {
            if (fileDescriptor >= 0)
            {
                context[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
                _ = KernelMemoryCompatExports.KernelClose(context);
            }

            _ = KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(hostRoot))
            {
                Directory.Delete(hostRoot, recursive: true);
            }
        }
    }
}
