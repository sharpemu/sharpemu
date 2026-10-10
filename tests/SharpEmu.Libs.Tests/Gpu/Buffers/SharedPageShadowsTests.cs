// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class SharedPageShadowsTests
{
    private const ulong PageBytes = 64;
    private const ulong Page = 0x1000;

    [Fact]
    public void UploadCopiesOnlyTheBytesTheCpuChanged()
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        var gpu = Fill(0x11);
        Assert.True(shadows.Adopt(Page, owner, gpu));

        // The CPU changed bytes 4..7 and 40; the shader's bytes elsewhere must survive the upload.
        var staged = Fill(0x11);
        staged.AsSpan(4, 4).Fill(0xAA);
        staged[40] = 0xBB;
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, owner, Page, staged, tick: 3);

        Assert.Equal([new BufferCopy(4, 4, 4), new BufferCopy(40, 40, 1)], copies);
        Assert.Equal(PageBytes - 5, (ulong)shadows.SkippedUploadBytes);
    }

    [Fact]
    public void UploadKeepsCopiesOutsideSharedPagesWhole()
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page + PageBytes, owner, Fill(0)));

        // One copy covers the page before, the shared page and the page after; it stages at 0x100.
        var staged = new byte[0x100 + 3 * PageBytes];
        staged[0x100 + PageBytes + 9] = 7;
        var copies = new List<BufferCopy> { new(0x100, 0, 3 * PageBytes), new(0, 0x500, 8) };
        shadows.FilterUpload(copies, owner, Page, staged, tick: 1);

        Assert.Equal(
            [
                new BufferCopy(0x100, 0, PageBytes),
                new BufferCopy(0x100 + PageBytes + 9, PageBytes + 9, 1),
                new BufferCopy(0x100 + 2 * PageBytes, 2 * PageBytes, PageBytes),
                new BufferCopy(0, 0x500, 8),
            ],
            copies);
    }

    [Fact]
    public void UploadIntoANewOwnerCopiesTheWholePage()
    {
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page, new object(), Fill(0x11)));
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, new object(), Page, Fill(0x11), tick: 1);
        Assert.Equal([new BufferCopy(0, 0, PageBytes)], copies);
    }

    [Fact]
    public void PullMergesShaderBytesWhereTheCpuLeftThemAlone()
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page, owner, Fill(0x11)));

        // The shader wrote bytes 8..15; the CPU meanwhile wrote byte 10 (it keeps it) and byte 50.
        var gpu = Fill(0x11);
        gpu.AsSpan(8, 8).Fill(0x22);
        var guest = Fill(0x11);
        guest[10] = 0x33;
        guest[50] = 0x44;
        var writes = new List<(int Offset, int Length)>();
        var merged = shadows.PullGpuWrites(Page, owner, gpu, guest, completedTick: 0, (offset, length) => writes.Add((offset, length)));

        Assert.Equal(7, merged);
        Assert.Equal([(8, 2), (11, 5)], writes);
        var expected = Fill(0x11);
        expected.AsSpan(8, 8).Fill(0x22);
        expected[10] = 0x33;
        expected[50] = 0x44;
        Assert.Equal(expected, guest);

        // The next upload carries both CPU bytes, including the one the shader also wrote.
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, owner, Page, guest, tick: 1);
        Assert.Equal([new BufferCopy(10, 10, 1), new BufferCopy(50, 50, 1)], copies);
    }

    [Fact]
    public void PullWaitsForStagedUploadsToExecute()
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page, owner, Fill(0)));
        var staged = Fill(0);
        staged[3] = 9;
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, owner, Page, staged, tick: 5);

        // The GPU copy does not hold the staged byte yet; it must not read as a shader change.
        var guest = staged.ToArray();
        Assert.Equal(0, shadows.PullGpuWrites(Page, owner, Fill(0), guest, completedTick: 4, (_, _) => { }));
        Assert.Equal(9, guest[3]);
        var gpu = staged.ToArray();
        gpu[20] = 1;
        Assert.Equal(1, shadows.PullGpuWrites(Page, owner, gpu, guest, completedTick: 5, (_, _) => { }));
        Assert.Equal(9, guest[3]);
        Assert.Equal(1, guest[20]);
    }

    [Fact]
    public void ADownloadMakesTheShadowAgreeWithTheGpuCopy()
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page, owner, Fill(0)));

        // The shader writes bytes 16..23 later; the CPU write that follows downloads them first.
        var downloaded = new byte[8];
        Array.Fill(downloaded, (byte)0x55);
        shadows.NoteDownloaded(Page + 16, downloaded);
        var staged = Fill(0);
        staged.AsSpan(16, 8).Fill(0x55);
        staged[30] = 0x66;
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, owner, Page, staged, tick: 2);
        Assert.Equal([new BufferCopy(30, 30, 1)], copies);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(3UL)]
    public void AnEliminatedUploadDoesNotDelayCompletedShaderWrites(ulong lastUploadTick)
    {
        var owner = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        var guest = Fill(0);
        Assert.True(shadows.Adopt(Page, owner, guest));
        if (lastUploadTick != 0)
        {
            guest[3] = 9;
            var first = new List<BufferCopy> { new(0, 0, PageBytes) };
            shadows.FilterUpload(first, owner, Page, guest, lastUploadTick);
            Assert.Equal([new BufferCopy(3, 3, 1)], first);
        }

        // Hot CPU pages can be offered for upload on every draw, even when no bytes changed.
        // Filtering removes this upload entirely: its future tick must not gate a GPU merge.
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, owner, Page, guest, tick: 5);
        Assert.Empty(copies);
        var gpu = guest.ToArray();
        gpu[20] = 0x55;
        var writes = new List<(int Offset, int Length)>();
        Assert.Equal(1, shadows.PullGpuWrites(Page, owner, gpu, guest, lastUploadTick,
            (offset, length) => writes.Add((offset, length))));
        Assert.Equal([(20, 1)], writes);
        Assert.Equal(0x55, guest[20]);
    }

    [Fact]
    public void AdoptStopsAtCapacity()
    {
        var shadows = new SharedPageShadows(PageBytes, 1);
        Assert.True(shadows.Adopt(Page, new object(), Fill(0)));
        Assert.False(shadows.Adopt(Page + PageBytes, new object(), Fill(0)));
        Assert.True(shadows.Contains(Page));
        Assert.False(shadows.Contains(Page + PageBytes));
    }

    [Fact]
    public void ABufferCopyTransfersTheBaselineAndWaitsForTheCopy()
    {
        var old = new object();
        var replacement = new object();
        var shadows = new SharedPageShadows(PageBytes, 4);
        Assert.True(shadows.Adopt(Page, old, Fill(0x11)));
        shadows.NoteOwnerCopy(old, replacement, tick: 5);
        var guest = Fill(0x11);
        guest[40] = 0xAA;
        var copies = new List<BufferCopy> { new(0, 0, PageBytes) };
        shadows.FilterUpload(copies, replacement, Page, guest, tick: 5);
        Assert.Equal([new BufferCopy(40, 40, 1)], copies);
        var gpu = Fill(0x11);
        gpu[20] = 0x55;
        gpu[40] = 0xAA;
        Assert.Equal(0, shadows.PullGpuWrites(Page, replacement, gpu, guest, 4, (_, _) => { }));
        Assert.Equal(1, shadows.PullGpuWrites(Page, replacement, gpu, guest, 5, (_, _) => { }));
        Assert.Equal(0x55, guest[20]);
        Assert.Equal(0xAA, guest[40]);
    }

    private static byte[] Fill(byte value)
    {
        var bytes = new byte[PageBytes];
        Array.Fill(bytes, value);
        return bytes;
    }
}
