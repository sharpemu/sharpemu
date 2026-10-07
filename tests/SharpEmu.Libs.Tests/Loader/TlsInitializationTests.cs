// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class TlsInitializationTests
{
    [Fact]
    public void LoadSeedsEachThreadWithRelocatedInitializationBytes()
    {
        try
        {
            var memory = new VirtualMemory();
            var image = new SelfLoader().Load(CreateImage(), memory);
            var imageBase = image.EntryPoint - image.ElfHeader.EntryPoint;
            var expectedPointer = imageBase + 0x600;
            var mappedPointer = new byte[8];
            Assert.True(memory.TryRead(imageBase + 0x500, mappedPointer));
            Assert.Equal(expectedPointer, BinaryPrimitives.ReadUInt64LittleEndian(mappedPointer));
            Assert.Equal(expectedPointer, BinaryPrimitives.ReadUInt64LittleEndian(GuestTlsTemplate.InitImage));

            var context = new CpuContext(new FakeCpuMemory(0x10000, 0x40000), Generation.Gen5);
            foreach (var threadPointer in new ulong[] { 0x20000, 0x30000 })
            {
                GuestTlsTemplate.SeedThreadBlock(context, threadPointer);
                var threadBytes = new byte[0x20];
                Assert.True(context.Memory.TryRead(threadPointer - image.TlsStaticOffset, threadBytes));
                Assert.Equal(expectedPointer, BinaryPrimitives.ReadUInt64LittleEndian(threadBytes));
                Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(threadBytes.AsSpan(8)));
                Assert.All(threadBytes[16..], value => Assert.Equal(0, value));
                Assert.True(context.Memory.TryWrite(threadPointer - image.TlsStaticOffset, new byte[8]));
            }
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void InitializationUpdatePreservesLayoutAndRejectsLiveThreadChanges()
    {
        try
        {
            GuestTlsTemplate.Reset();
            var staticOffset = GuestTlsTemplate.RegisterModule(1, new byte[8], 0x20, 0x10);
            var generation = GuestTlsTemplate.Generation;
            byte[] initializationImage = [1, 2, 3, 4, 5, 6, 7, 8];
            GuestTlsTemplate.UpdateInitializationImage(1, initializationImage);
            Assert.Equal(generation, GuestTlsTemplate.Generation);
            Assert.Equal(staticOffset, GuestTlsTemplate.BlockSize);
            initializationImage[0] = 0;
            Assert.Equal(1, GuestTlsTemplate.InitImage[0]);
            Assert.Throws<ArgumentException>(() => GuestTlsTemplate.UpdateInitializationImage(1, new byte[9]));
            Assert.Throws<ArgumentOutOfRangeException>(() => GuestTlsTemplate.UpdateInitializationImage(2, new byte[8]));

            var context = new CpuContext(new FakeCpuMemory(0x10000, 0x20000), Generation.Gen5);
            GuestTlsTemplate.SeedThreadBlock(context, 0x20000);
            Assert.Throws<InvalidOperationException>(() => GuestTlsTemplate.UpdateInitializationImage(1, new byte[8]));
            GuestTlsTemplate.RegisterModule(2, new byte[8], 0x20, 0x10);
            GuestTlsTemplate.UpdateInitializationImage(2, initializationImage);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Theory]
    [InlineData(0x21UL, 0x20UL, 0x500UL)]
    [InlineData(8UL, 0x20UL, 0x2000UL)]
    public void LoadRejectsInvalidInitializationRange(ulong fileSize, ulong memorySize, ulong virtualAddress)
    {
        try
        {
            var image = CreateImage();
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0 + 0x10), virtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0 + 0x20), fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0 + 0x28), memorySize);
            Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(image, new VirtualMemory()));
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    private static byte[] CreateImage()
    {
        var image = new byte[0x1000];
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 9, 2 }.CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 62);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 3);
        WriteProgramHeader(image.AsSpan(0x40), 1, 0, 0x1000, 0x1000);
        WriteProgramHeader(image.AsSpan(0x78), 2, 0x200, 0x40, 0x40);
        WriteProgramHeader(image.AsSpan(0xB0), 7, 0x500, 0x10, 0x20);
        WriteDynamicEntry(image.AsSpan(0x200), 7, 0x300);
        WriteDynamicEntry(image.AsSpan(0x210), 8, 0x30);
        WriteDynamicEntry(image.AsSpan(0x220), 9, 0x18);
        WriteRelocation(image.AsSpan(0x300), 0x500, 8, 0x600);
        WriteRelocation(image.AsSpan(0x318), 0x508, 16, 0);
        return image;
    }

    private static void WriteProgramHeader(Span<byte> header, uint type, ulong offset, ulong fileSize, ulong memorySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 6);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x30..], 0x10);
    }

    private static void WriteDynamicEntry(Span<byte> entry, ulong tag, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], value);
    }

    private static void WriteRelocation(Span<byte> entry, ulong targetOffset, ulong type, ulong addend)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(entry, targetOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], type);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], addend);
    }
}
