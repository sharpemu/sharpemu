// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AprStreamingContractTests
{
    [Fact]
    public void ResolveStatAndReadFile_UsesSharedAprFileId()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong statAddress = memoryBase + 0x900;
        const ulong commandBufferAddress = memoryBase + 0x1000;
        const ulong recordBufferAddress = memoryBase + 0x1100;
        const ulong destinationAddress = memoryBase + 0x2000;
        const ulong stackAddress = memoryBase + 0x3000;
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        var hostPath = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(pathAddress, hostPath);
            WriteUInt64(memory, pathListAddress, pathAddress);

            context[CpuRegister.Rdi] = pathListAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = idsAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprResolveFilepathsToIds(context));

            Span<byte> idBytes = stackalloc byte[sizeof(uint)];
            Assert.True(memory.TryRead(idsAddress, idBytes));
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(idBytes);
            Assert.NotEqual(uint.MaxValue, fileId);

            context[CpuRegister.Rdi] = fileId;
            context[CpuRegister.Rsi] = statAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprGetFileStat(context));

            Span<byte> stat = stackalloc byte[120];
            Assert.True(memory.TryRead(statAddress, stat));
            Assert.Equal(fileContents.Length, BinaryPrimitives.ReadInt64LittleEndian(stat[72..]));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;

            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

            const ulong readOffset = 2;
            const ulong readSize = 4;
            WriteUInt64(memory, stackAddress + sizeof(ulong), readOffset);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destinationAddress;
            context[CpuRegister.R9] = readSize;

            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            Span<byte> destination = stackalloc byte[(int)readSize];
            Assert.True(memory.TryRead(destinationAddress, destination));
            Assert.Equal(fileContents.AsSpan((int)readOffset, (int)readSize), destination);

            Span<byte> record = stackalloc byte[0x30];
            Assert.True(memory.TryRead(recordBufferAddress, record));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(record));
            Assert.Equal(fileId, BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
            Assert.Equal(destinationAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]));
            Assert.Equal(readOffset, BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]));
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
