// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AprImportStackArgumentTests
{
    public AprImportStackArgumentTests() => AmprFileRegistry.ClearForTests();

    [Fact]
    public void ReadFileUsesCapturedStackArgumentWhenImportRspIsUnavailable()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBuffer = memoryBase + 0x100;
        const ulong recordBuffer = memoryBase + 0x200;
        const ulong destination = memoryBase + 0x400;
        const ulong fileOffset = 0x1234;
        const int readSize = 0x20;

        var fileContents = new byte[checked((int)fileOffset + readSize)];
        for (var index = 0; index < fileContents.Length; index++)
        {
            fileContents[index] = unchecked((byte)(index * 31));
        }

        var hostPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-stack-argument-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, fileContents);

        try
        {
            var fileId = AmprFileRegistry.Register(
                $"$/stack-argument-{Guid.NewGuid():N}.bin",
                hostPath);
            var memory = new FakeCpuMemory(memoryBase, 0x1000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = commandBuffer;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

            context[CpuRegister.Rdi] = commandBuffer;
            context[CpuRegister.Rsi] = recordBuffer;
            context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            context[CpuRegister.Rsp] = 0x7FFF_FFFF_F000;
            context.SetImportStackArguments(fileOffset, 0, 0, 0, 0, 0);
            context[CpuRegister.Rdi] = commandBuffer;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destination;
            context[CpuRegister.R9] = readSize;

            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));
            Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBuffer));

            Span<byte> actual = stackalloc byte[readSize];
            Assert.True(memory.TryRead(destination, actual));
            Assert.Equal(fileContents.AsSpan((int)fileOffset, readSize), actual);
        }
        finally
        {
            File.Delete(hostPath);
        }
    }
}
