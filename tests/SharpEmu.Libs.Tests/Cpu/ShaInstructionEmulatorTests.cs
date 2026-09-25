// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpEmu.Core.Cpu.Emulation;
using Xunit;
using static SharpEmu.Core.Cpu.Emulation.ShaInstructionEmulator;

namespace SharpEmu.Libs.Tests.Cpu;

// The hosts that need this fallback cannot run SHA-NI, so each test builds a complete hash
// from the emulated instructions in the order SHA-NI code uses them, and compares the digest
// with the .NET implementation. A wrong lane order or round step changes the digest.
public sealed class ShaInstructionEmulatorTests
{
    public static TheoryData<int> MessageLengths => new() { 0, 3, 55, 56, 64, 119, 1000 };

    [Theory]
    [MemberData(nameof(MessageLengths))]
    public void Sha1Instructions_ProduceTheSha1Digest(int length)
    {
        var message = CreateMessage(length);
        var state = new uint[] { 0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0 };
        foreach (var block in PadMessage(message))
        {
            Sha1Block(state, block);
        }

        var digest = new byte[20];
        for (var i = 0; i < state.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(4 * i), state[i]);
        }

        Assert.Equal(SHA1.HashData(message), digest);
    }

    [Theory]
    [MemberData(nameof(MessageLengths))]
    public void Sha256Instructions_ProduceTheSha256Digest(int length)
    {
        var message = CreateMessage(length);
        var state = new uint[]
        {
            0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
        };
        foreach (var block in PadMessage(message))
        {
            Sha256Block(state, block);
        }

        var digest = new byte[32];
        for (var i = 0; i < state.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(4 * i), state[i]);
        }

        Assert.Equal(SHA256.HashData(message), digest);
    }

    [Fact]
    public void Sha1Nexte_AddsRotatedEToTheTopDwordOnly()
    {
        var result = Sha1Nexte(FromDwords(0, 0, 0, 0x8000_0001), FromDwords(1, 2, 3, 4));

        Assert.Equal(FromDwords(1, 2, 3, 4 + 0x6000_0000), result);
    }

    // SHA1RNDS4: ABCD in source1 (A in dword 3), W0+E..W3 in source2 (W0+E in dword 3).
    private static void Sha1Block(uint[] state, byte[] block)
    {
        var messages = new UInt128[20];
        for (var i = 0; i < 4; i++)
        {
            messages[i] = FromDwords(
                ReadWord(block, 4 * i + 3), ReadWord(block, 4 * i + 2), ReadWord(block, 4 * i + 1), ReadWord(block, 4 * i));
        }

        for (var i = 4; i < 20; i++)
        {
            messages[i] = Sha1Msg2(Sha1Msg1(messages[i - 4], messages[i - 3]) ^ messages[i - 2], messages[i - 1]);
        }

        var abcd = FromDwords(state[3], state[2], state[1], state[0]);
        var abcdSave = abcd;
        var eSave = FromDwords(0, 0, 0, state[4]);
        var e = Add(eSave, messages[0]);
        for (var group = 0; group < 20; group++)
        {
            var previous = abcd;
            abcd = Sha1Rnds4(abcd, e, group / 5);
            e = Sha1Nexte(previous, group < 19 ? messages[group + 1] : eSave);
        }

        abcd = Add(abcd, abcdSave);
        state[0] = Dword(abcd, 3);
        state[1] = Dword(abcd, 2);
        state[2] = Dword(abcd, 1);
        state[3] = Dword(abcd, 0);
        state[4] = Dword(e, 3);
    }

    // SHA256RNDS2 keeps the state as ABEF and CDGH with the first letter in dword 3.
    private static void Sha256Block(uint[] state, byte[] block)
    {
        var messages = new UInt128[16];
        for (var i = 0; i < 4; i++)
        {
            messages[i] = FromDwords(
                ReadWord(block, 4 * i), ReadWord(block, 4 * i + 1), ReadWord(block, 4 * i + 2), ReadWord(block, 4 * i + 3));
        }

        for (var i = 4; i < 16; i++)
        {
            var w7 = FromDwords(
                Dword(messages[i - 2], 1), Dword(messages[i - 2], 2), Dword(messages[i - 2], 3), Dword(messages[i - 1], 0));
            messages[i] = Sha256Msg2(Add(Sha256Msg1(messages[i - 4], messages[i - 3]), w7), messages[i - 1]);
        }

        var abef = FromDwords(state[5], state[4], state[1], state[0]);
        var cdgh = FromDwords(state[7], state[6], state[3], state[2]);
        var abefSave = abef;
        var cdghSave = cdgh;
        for (var group = 0; group < 16; group++)
        {
            var wk = Add(messages[group], FromDwords(
                RoundConstants[4 * group], RoundConstants[4 * group + 1],
                RoundConstants[4 * group + 2], RoundConstants[4 * group + 3]));
            cdgh = Sha256Rnds2(cdgh, abef, wk);
            abef = Sha256Rnds2(abef, cdgh, wk >> 64);
        }

        abef = Add(abef, abefSave);
        cdgh = Add(cdgh, cdghSave);
        state[0] = Dword(abef, 3);
        state[1] = Dword(abef, 2);
        state[4] = Dword(abef, 1);
        state[5] = Dword(abef, 0);
        state[2] = Dword(cdgh, 3);
        state[3] = Dword(cdgh, 2);
        state[6] = Dword(cdgh, 1);
        state[7] = Dword(cdgh, 0);
    }

    private static UInt128 Add(UInt128 left, UInt128 right) => FromDwords(
        Dword(left, 0) + Dword(right, 0), Dword(left, 1) + Dword(right, 1),
        Dword(left, 2) + Dword(right, 2), Dword(left, 3) + Dword(right, 3));

    private static uint ReadWord(byte[] block, int index) =>
        BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4 * index));

    private static byte[] CreateMessage(int length)
    {
        var message = new byte[length];
        for (var i = 0; i < length; i++)
        {
            message[i] = (byte)(i * 31 + 7);
        }

        return message;
    }

    private static IEnumerable<byte[]> PadMessage(byte[] message)
    {
        var paddedLength = (message.Length + 8) / 64 * 64 + 64;
        var padded = new byte[paddedLength];
        message.CopyTo(padded, 0);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(paddedLength - 8), (ulong)message.Length * 8);
        for (var offset = 0; offset < paddedLength; offset += 64)
        {
            yield return padded[offset..(offset + 64)];
        }
    }

    private static readonly uint[] RoundConstants =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };
}
