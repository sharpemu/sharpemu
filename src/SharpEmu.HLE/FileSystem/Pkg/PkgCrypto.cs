// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SharpEmu.HLE.FileSystem.Pkg;

// Key derivation and the sector cipher of the PFS image. Ported from shadPS4
// (src/core/crypto/crypto.cpp).
public static class PkgCrypto
{
    public const int SectorSize = 0x1000;

    // The first 16 sectors of the PFS image (its header block) are stored in the clear.
    public const long FirstEncryptedSector = 16;

    // Raw RSA with PKCS#1 v1.5 (type 2) unpadding, which is what RSAES_PKCS1v15 does.
    public static byte[] RsaDecrypt(ReadOnlySpan<byte> ciphertext, string modulusHex, string privateExponentHex)
    {
        var n = new BigInteger(Convert.FromHexString(modulusHex), isUnsigned: true, isBigEndian: true);
        var d = new BigInteger(Convert.FromHexString(privateExponentHex), isUnsigned: true, isBigEndian: true);
        var c = new BigInteger(ciphertext, isUnsigned: true, isBigEndian: true);
        var length = (int)((n.GetBitLength() + 7) / 8);
        var plain = BigInteger.ModPow(c, d, n);
        var block = new byte[length];
        plain.TryWriteBytes(block.AsSpan(length - plain.GetByteCount(isUnsigned: true)),
            out _, isUnsigned: true, isBigEndian: true);
        return UnpadPkcs1(block);
    }

    internal static byte[] UnpadPkcs1(byte[] block)
    {
        if (block.Length < 11 || block[0] != 0 || block[1] != 2)
        {
            throw new InvalidDataException("RSA block has invalid PKCS#1 padding.");
        }

        var index = 2;
        while (index < block.Length && block[index] != 0)
        {
            index++;
        }

        if (index >= block.Length || index < 10)
        {
            throw new InvalidDataException("RSA block has invalid PKCS#1 padding.");
        }

        return block[(index + 1)..];
    }

    // dk3 = RSA(PkgDerivedKey3)(entry key #3)
    public static byte[] DeriveDk3(ReadOnlySpan<byte> entryKey3) =>
        Truncate(RsaDecrypt(entryKey3, PkgKeys.DerivedKey3Modulus, PkgKeys.DerivedKey3PrivateExponent), 32);

    // ekpfs = RSA(Fake)(AES-CBC(SHA256(imageKeyEntryRow || dk3))(image key))
    public static byte[] DeriveEkpfs(ReadOnlySpan<byte> imageKeyEntryRow, ReadOnlySpan<byte> dk3, ReadOnlySpan<byte> imageKey)
    {
        if (imageKey.Length % 16 != 0)
        {
            throw new InvalidDataException("PKG image key is not a whole number of AES blocks.");
        }

        Span<byte> ivKeyInput = stackalloc byte[64];
        imageKeyEntryRow[..PkgEntry.Size_].CopyTo(ivKeyInput);
        dk3[..32].CopyTo(ivKeyInput[32..]);
        var ivKey = SHA256.HashData(ivKeyInput);

        using var aes = Aes.Create();
        aes.Key = ivKey[16..32];
        byte[] decrypted;
        try
        {
            decrypted = aes.DecryptCbc(imageKey, ivKey.AsSpan(0, 16), PaddingMode.None);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Could not decrypt the PKG image key.", exception);
        }
        return Truncate(RsaDecrypt(decrypted, PkgKeys.FakeModulus, PkgKeys.FakePrivateExponent), 32);
    }

    // The PFS data and tweak keys: HMAC-SHA256(ekpfs, le32(1) || seed), tweak first, data second.
    public static (byte[] DataKey, byte[] TweakKey) DerivePfsKeys(ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed)
    {
        Span<byte> input = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 1);
        seed[..16].CopyTo(input[4..]);
        var hash = HMACSHA256.HashData(ekpfs, input);
        return (hash[16..32], hash[..16]);
    }

    // AES-128-XTS over `data` (a whole number of 16-byte blocks), in place. `sector` is the
    // tweak: the sector number of the first byte.
    public static void XtsDecrypt(Aes dataCipher, Aes tweakCipher, Span<byte> data, ulong sector)
    {
        if (data.Length % 16 != 0)
        {
            throw new ArgumentException("XTS data must be a multiple of 16 bytes.", nameof(data));
        }

        Span<byte> tweak = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(tweak, sector);
        tweakCipher.EncryptEcb(tweak, tweak, PaddingMode.None);

        var blocks = data.Length / 16;
        var tweaks = blocks <= 256 ? stackalloc byte[blocks * 16] : new byte[blocks * 16];
        for (var i = 0; i < blocks; i++)
        {
            tweak.CopyTo(tweaks.Slice(i * 16, 16));
            MultiplyTweak(tweak);
        }

        Xor(data, tweaks);
        dataCipher.DecryptEcb(data, data, PaddingMode.None);
        Xor(data, tweaks);
    }

    // Multiplication by x in GF(2^128), little-endian bit order.
    internal static void MultiplyTweak(Span<byte> tweak)
    {
        byte carry = 0;
        for (var i = 0; i < 16; i++)
        {
            var next = (byte)(tweak[i] >> 7);
            tweak[i] = (byte)((tweak[i] << 1) | carry);
            carry = next;
        }

        if (carry != 0)
        {
            tweak[0] ^= 0x87;
        }
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> mask)
    {
        for (var i = 0; i < data.Length; i += 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(mask[i..]);
            BinaryPrimitives.WriteUInt64LittleEndian(data[i..], value);
        }
    }

    private static byte[] Truncate(byte[] value, int length) =>
        value.Length >= length ? value[..length] : throw new InvalidDataException("Decrypted key is too short.");
}
