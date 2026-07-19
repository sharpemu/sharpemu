// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

[CollectionDefinition(GuestTlsTemplateStateCollection.Name, DisableParallelization = true)]
public sealed class GuestTlsTemplateStateCollection
{
    public const string Name = "GuestTlsTemplateState";
}

[Collection(GuestTlsTemplateStateCollection.Name)]
public sealed class GuestTlsTemplateTests
{
    [Fact]
    public void StartupReservationAcceptsTlsSpansLargerThanOneHostPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x1870,
                alignment: 0x10);

            Assert.Equal(0x1870UL, staticOffset);
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    // Regression test for the loader ordering bug fixed this session: a module's PT_TLS
    // .tdata bytes can contain relocatable pointers that aren't resolved until after the
    // loader's initial TLS registration snapshot is taken. UpdateInitImage lets the loader
    // correct that snapshot post-relocation - this verifies the correction actually reaches
    // a thread's resolved TLS storage, and that it doesn't disturb the module's already
    // -assigned static offset (which relocation processing depends on staying stable).
    [Fact]
    public void UpdateInitImageReplacesBytesSeenByLaterThreadsWithoutMovingStaticOffset()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var preRelocationImage = new byte[8]; // all zero, as an unresolved pointer reloc would leave it
            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: preRelocationImage,
                memorySize: 8,
                alignment: 8);

            var postRelocationImage = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
            GuestTlsTemplate.UpdateInitImage(1, postRelocationImage);

            Assert.True(GuestTlsTemplate.TryGetStaticOffset(1, out var offsetAfterUpdate));
            Assert.Equal(staticOffset, offsetAfterUpdate);

            var memory = new FakeCpuMemory(0x1_0000_0000, 0x10000);
            var context = new CpuContext(memory, Generation.Gen5)
            {
                FsBase = 0x1_0000_8000,
            };

            var resolved = GuestTlsTemplate.ResolveAddress(context, moduleId: 1, offset: 0);
            Assert.NotEqual(0UL, resolved);

            var resolvedBytes = new byte[postRelocationImage.Length];
            Assert.True(memory.TryRead(resolved, resolvedBytes));
            Assert.Equal(postRelocationImage, resolvedBytes);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
