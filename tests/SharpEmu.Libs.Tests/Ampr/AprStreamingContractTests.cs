// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AprStreamingContractTests
{
    public AprStreamingContractTests() => AmprFileRegistry.ClearForTests();

    [Fact]
    public void AprCommandBufferConstructor_ReturnsSuccessInRax()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rax] = ulong.MaxValue;
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = commandBufferAddress + 0x18;
        context[CpuRegister.Rdx] = commandBufferAddress + 0x20;

        Assert.Equal(0, AmprExports.AprCommandBufferConstructor(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0UL, ReadUInt64(memory, commandBufferAddress + 0x18));
        Assert.Equal(0UL, ReadUInt64(memory, commandBufferAddress + 0x20));
    }

    [Fact]
    public void ConstructingAtBackingAlias_DoesNotDeleteOwnerState()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong ownerAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x300;
        const ulong completionAddress = memoryBase + 0x800;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        AmprExports.ResetRuntimeState();

        context[CpuRegister.Rdi] = ownerAddress;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        // The backing address is an alias used when commands are submitted.
        // Reusing that address as another command-buffer object must not remove
        // the original owner's retained state.
        context[CpuRegister.Rdi] = recordBufferAddress;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = completionAddress;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddress0400(context));
        Assert.Equal(0x20U, ReadUInt32(memory, ownerAddress + 0x04));
        Assert.Equal(1U, ReadUInt32(memory, ownerAddress + 0x08));
    }

    [Fact]
    public void CommandBufferReset_RestoresRetainedStateAfterGuestHeaderRemap()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x300;
        const ulong completionAddress = memoryBase + 0x800;
        const ulong recordBufferSize = 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        AmprExports.ResetRuntimeState();

        context[CpuRegister.Rdi] = commandBufferAddress;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = recordBufferSize;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        // An AMM remap may clear the guest-visible header while the APR object
        // and its SetBuffer binding remain live in the host-side state.
        Span<byte> clearedHeader = stackalloc byte[0x18];
        clearedHeader.Clear();
        Assert.True(memory.TryWrite(commandBufferAddress, clearedHeader));

        context[CpuRegister.Rdi] = commandBufferAddress;
        Assert.Equal(0, AmprExports.CommandBufferReset(context));
        Assert.Equal((uint)recordBufferSize, ReadUInt32(memory, commandBufferAddress + 0x0C));
        Assert.Equal(0U, ReadUInt32(memory, commandBufferAddress + 0x04));
        Assert.Equal(0U, ReadUInt32(memory, commandBufferAddress + 0x08));
        Assert.Equal(recordBufferAddress, ReadUInt64(memory, commandBufferAddress + 0x10));

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = completionAddress;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddress0400(context));
        Assert.Equal(0x20U, ReadUInt32(memory, commandBufferAddress + 0x04));
        Assert.Equal(1U, ReadUInt32(memory, commandBufferAddress + 0x08));
    }

    [Fact]
    public void ResolveStatAndReadFile_UsesSharedAprFileId()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong statAddress = memoryBase + 0x900;
        const ulong sizeAddress = memoryBase + 0x9A0;
        const ulong commandBufferAddress = memoryBase + 0x1000;
        const ulong recordBufferAddress = memoryBase + 0x1100;
        const ulong destinationAddress = memoryBase + 0x2000;
        const ulong stackAddress = memoryBase + 0x3000;
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        // The kernel FS resolver default-denies raw absolute host paths, so the
        // guest addresses the file through a registered mount instead of handing
        // in a bare host temp path.
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);
        var guestPath = $"{mountPoint}/{fileName}";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(pathAddress, guestPath);
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

            WriteUInt64(memory, sizeAddress, 0xDEAD_BEEF);
            context[CpuRegister.Rdi] = fileId;
            context[CpuRegister.Rsi] = sizeAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprGetFileSize(context));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizeAddress));

            context[CpuRegister.Rdi] = 0xFFFF_FFFE;
            Assert.NotEqual(0, KernelMemoryCompatExports.KernelAprGetFileSize(context));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;

            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

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
            Assert.Equal(new byte[(int)readSize].AsSpan(), destination);

            Assert.Equal(0x14U, ReadUInt32(memory, commandBufferAddress + 0x04));
            Assert.Equal(1U, ReadUInt32(memory, commandBufferAddress + 0x08));
            Assert.Equal(0x17U, ReadUInt32(memory, recordBufferAddress));

            Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBufferAddress));

            Assert.True(memory.TryRead(destinationAddress, destination));
            Assert.Equal(fileContents.AsSpan((int)readOffset, (int)readSize), destination);
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadFile_UsesCapturedNativeImportStackArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong destinationAddress = memoryBase + 0x400;
        const ulong fileOffset = 0x1234;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = commandBufferAddress;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        context[CpuRegister.Rsp] = 0x7FFF_FFFF_F000;
        context.SetImportStackArguments(fileOffset, 0, 0, 0, 0, 0);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rcx] = 0x8000_0001;
        context[CpuRegister.R8] = destinationAddress;
        context[CpuRegister.R9] = 0x20;

        Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));
        Assert.Equal(0x14u, ReadUInt32(memory, commandBufferAddress + 4));
    }

    [Fact]
    public void ReadFile_SequentialOffsetSentinel_DoesNotFaultDuringAppend()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong destinationAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = commandBufferAddress;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        context[CpuRegister.Rsp] = 0x7FFF_FFFF_F000;
        context.SetImportStackArguments(ulong.MaxValue, 0, 0, 0, 0, 0);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rcx] = 0x8000_0001;
        context[CpuRegister.R8] = destinationAddress;
        context[CpuRegister.R9] = 0x20;

        Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));
        Assert.Equal(0x18u, ReadUInt32(memory, commandBufferAddress + 4));
    }

    [Fact]
    public void ReadFileAndWriteAddress_ExecuteInRecordedOrderAtSubmit()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong destinationAddress = memoryBase + 0x1000;
        const ulong stackAddress = memoryBase + 0x1800;
        const ulong completionValue = 0x8877_6655_4433_2211;
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-order-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, [1, 2, 3, 4, 5, 6, 7, 8]);

        try
        {
            var fileId = AmprFileRegistry.Register($"$/order-{Guid.NewGuid():N}.bin", hostPath);
            var memory = new FakeCpuMemory(memoryBase, 0x3000);
            var context = new CpuContext(memory, Generation.Gen5);

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            WriteUInt64(memory, stackAddress + sizeof(ulong), 0);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destinationAddress;
            context[CpuRegister.R9] = sizeof(ulong);
            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = destinationAddress;
            context[CpuRegister.Rdx] = completionValue;
            Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(context));

            Assert.Equal(0UL, ReadUInt64(memory, destinationAddress));
            Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBufferAddress));
            Assert.Equal(completionValue, ReadUInt64(memory, destinationAddress));
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public void SequentialReadOffsets_AreResolvedInSubmitOrder()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong firstDestination = memoryBase + 0x1000;
        const ulong secondDestination = memoryBase + 0x1100;
        const ulong stackAddress = memoryBase + 0x1800;
        const ulong sequentialOffset = ulong.MaxValue;
        const ulong readSize = 3;
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-sequential-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, fileContents);

        try
        {
            var fileId = AmprFileRegistry.Register($"$/sequential-{Guid.NewGuid():N}.bin", hostPath);
            var memory = new FakeCpuMemory(memoryBase, 0x3000);
            var context = new CpuContext(memory, Generation.Gen5);

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            WriteUInt64(memory, stackAddress + sizeof(ulong), sequentialOffset);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = firstDestination;
            context[CpuRegister.R9] = readSize;
            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            context[CpuRegister.R8] = secondDestination;
            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBufferAddress));

            Span<byte> first = stackalloc byte[(int)readSize];
            Span<byte> second = stackalloc byte[(int)readSize];
            Assert.True(memory.TryRead(firstDestination, first));
            Assert.True(memory.TryRead(secondDestination, second));
            Assert.Equal(fileContents.AsSpan(0, (int)readSize), first);
            Assert.Equal(fileContents.AsSpan((int)readSize, (int)readSize), second);

            Assert.Equal(0x30U, ReadUInt32(memory, commandBufferAddress + 0x04));
            Assert.Equal(2U, ReadUInt32(memory, commandBufferAddress + 0x08));
            Assert.Equal(0x17U, ReadUInt32(memory, recordBufferAddress));
            Assert.Equal(0x17U, ReadUInt32(memory, recordBufferAddress + 0x18));
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public void SubmitAndGetResult_ReportsDeferredReadFailureAndRecordOffset()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong destinationAddress = memoryBase + 0x1000;
        const ulong resultAddress = memoryBase + 0x1200;
        const ulong submissionIdAddress = memoryBase + 0x1300;
        const ulong precedingMarkerAddress = memoryBase + 0x1400;
        const ulong trailingMarkerAddress = memoryBase + 0x1500;
        const ulong stackAddress = memoryBase + 0x1800;
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-error-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, [1, 2, 3, 4]);

        try
        {
            var fileId = AmprFileRegistry.Register($"$/error-{Guid.NewGuid():N}.bin", hostPath);
            var memory = new FakeCpuMemory(memoryBase, 0x3000);
            var context = new CpuContext(memory, Generation.Gen5);

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = precedingMarkerAddress;
            context[CpuRegister.Rdx] = 1;
            Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(context));

            WriteUInt64(memory, stackAddress + sizeof(ulong), 0);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destinationAddress;
            context[CpuRegister.R9] = 4;
            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = trailingMarkerAddress;
            context[CpuRegister.Rdx] = 1;
            Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(context));

            File.Delete(hostPath);

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = resultAddress;
            context[CpuRegister.Rcx] = submissionIdAddress;
            Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetResult(context));

            Assert.NotEqual(0U, ReadUInt32(memory, submissionIdAddress));
            Assert.Equal(
                unchecked((uint)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND),
                ReadUInt32(memory, resultAddress));
            Assert.Equal(0x20U, ReadUInt32(memory, resultAddress + sizeof(uint)));
            Assert.Equal(1UL, ReadUInt64(memory, precedingMarkerAddress));
            Assert.Equal(0UL, ReadUInt64(memory, trailingMarkerAddress));
        }
        finally
        {
            if (File.Exists(hostPath))
            {
                File.Delete(hostPath);
            }
        }
    }

    [Fact]
    public void ResolveFilepathsWithPrefixToIdsAndFileSizes_CombinesPrefixAndResolvesRealFile()
    {
        // Resource streamers call WithPrefix to join a directory prefix with a
        // relative asset path. Without HLE every call returned NOT_FOUND and no
        // asset received a real file id/size.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x80;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        byte[] fileContents = [1, 2, 3, 4, 5, 6];
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-prefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_prefix_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, mountPoint);
            memory.WriteCString(pathAddress, fileName);
            WriteUInt64(memory, pathListAddress, pathAddress);

            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;
            context[CpuRegister.R9] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveFilepathsWithPrefixToIdsAndFileSizes_MissingFile_FailsFastWithErrorIndex()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x80;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(prefixAddress, "/does-not-exist-prefix");
        memory.WriteCString(pathAddress, $"missing-{Guid.NewGuid():N}.bin");
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = prefixAddress;
        context[CpuRegister.Rsi] = pathListAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = idsAddress;
        context[CpuRegister.R8] = sizesAddress;
        context[CpuRegister.R9] = errorIndexAddress;

        Assert.Equal(
            -1,
            KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress));
        Assert.Equal(0ul, ReadUInt64(memory, sizesAddress));
        Assert.Equal(0u, ReadUInt32(memory, errorIndexAddress));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_MissingFile_FailsFastWithErrorIndex()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var missingHostPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-missing-{Guid.NewGuid():N}.bin");
        memory.WriteCString(pathAddress, missingHostPath);
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = pathListAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = idsAddress;
        context[CpuRegister.Rcx] = sizesAddress;
        context[CpuRegister.R8] = errorIndexAddress;

        Assert.Equal(-1, KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress));
        Assert.Equal(0ul, ReadUInt64(memory, sizesAddress));
        Assert.Equal(0u, ReadUInt32(memory, errorIndexAddress));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_InvalidErrorIndex_ReturnsMemoryFault()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var missingHostPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-missing-{Guid.NewGuid():N}.bin");
        memory.WriteCString(pathAddress, missingHostPath);
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = pathListAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = idsAddress;
        context[CpuRegister.Rcx] = sizesAddress;
        context[CpuRegister.R8] = memoryBase + 0x5000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_MissingMidBatch_StopsAtFailingEntry()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        byte[] fileContents = [1, 2, 3, 4, 5];
        // Entries 0 and 2 must resolve to a real file; the kernel FS resolver
        // default-denies raw absolute host paths, so the present file is reached
        // through a registered mount. The missing entry stays an unresolvable
        // path so the batch fails mid-way at index 1.
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);
        var guestPath = $"{mountPoint}/{fileName}";
        var missingGuestPath = $"{mountPoint}/missing-{Guid.NewGuid():N}.bin";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(memoryBase + 0x200, guestPath);
            memory.WriteCString(memoryBase + 0x400, missingGuestPath);
            memory.WriteCString(memoryBase + 0x600, guestPath);
            WriteUInt64(memory, pathListAddress, memoryBase + 0x200);
            WriteUInt64(memory, pathListAddress + 8, memoryBase + 0x400);
            WriteUInt64(memory, pathListAddress + 16, memoryBase + 0x600);
            WriteUInt32(memory, idsAddress + 8, 0x1234_5678);   // sentinel: entry 2 untouched
            WriteUInt64(memory, sizesAddress + 16, 0xDEAD);

            context[CpuRegister.Rdi] = pathListAddress;
            context[CpuRegister.Rsi] = 3;
            context[CpuRegister.Rdx] = idsAddress;
            context[CpuRegister.Rcx] = sizesAddress;
            context[CpuRegister.R8] = errorIndexAddress;

            Assert.Equal(-1, KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress + 4));
            Assert.Equal(0ul, ReadUInt64(memory, sizesAddress + 8));
            Assert.Equal(1u, ReadUInt32(memory, errorIndexAddress));
            Assert.Equal(0x1234_5678u, ReadUInt32(memory, idsAddress + 8));
            Assert.Equal(0xDEADul, ReadUInt64(memory, sizesAddress + 16));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void Register_AlsoPublishesApp0AndDollarPathAliases()
    {
        // Resolve may register "$/asset.bin" while cooked tables look up
        // FNV("/app0/asset.bin"). Both ids must map to the same host file.
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-alias-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, [1, 2, 3]);
        try
        {
            var dollarId = AmprFileRegistry.Register("$/weapons/demo.cani", hostPath);
            Assert.True(AmprFileRegistry.TryGetHostPath(dollarId, out var viaDollar));
            Assert.Equal(hostPath, viaDollar);

            var app0Id = AmprFileRegistry.ComputeFileId("/app0/weapons/demo.cani");
            Assert.NotEqual(dollarId, app0Id);
            Assert.True(AmprFileRegistry.TryGetHostPath(app0Id, out var viaApp0));
            Assert.Equal(hostPath, viaApp0);
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public void EnsureApp0Indexed_PublishesCookedApp0FileIds()
    {
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-index-{Guid.NewGuid():N}");
        var relativeDir = Path.Combine(mountRoot, "weapons");
        Directory.CreateDirectory(relativeDir);
        var hostPath = Path.Combine(relativeDir, "demo.cani");
        File.WriteAllBytes(hostPath, [9, 8, 7]);
        try
        {
            AmprFileRegistry.EnsureApp0Indexed(mountRoot);
            var cookedId = AmprFileRegistry.ComputeFileId("/app0/weapons/demo.cani");
            Assert.True(AmprFileRegistry.TryGetHostPath(cookedId, out var resolved));
            Assert.Equal(Path.GetFullPath(hostPath), Path.GetFullPath(resolved));
        }
        finally
        {
            Directory.Delete(mountRoot, recursive: true);
        }
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
