// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static partial class HttpExports
{
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80433060);
    private const int MaximumUriLength = 16 * 1024 - 1;

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct HttpUriComponents
    {
        [FieldOffset(0)] public int IsOpaque;
        [FieldOffset(8)] public ulong SchemeAddress;
        [FieldOffset(16)] public ulong UserNameAddress;
        [FieldOffset(24)] public ulong PasswordAddress;
        [FieldOffset(32)] public ulong HostNameAddress;
        [FieldOffset(40)] public ulong PathAddress;
        [FieldOffset(48)] public ulong QueryAddress;
        [FieldOffset(56)] public ulong FragmentAddress;
        [FieldOffset(64)] public ushort Port;
    }

    [SysAbiExport(
        Nid = "IWalAn-guFs",
        ExportName = "sceHttpUriParse",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int ParseHttpUri(CpuContext context)
    {
        var outputAddress = context[CpuRegister.Rdi];
        var sourceAddress = context[CpuRegister.Rsi];
        var poolAddress = context[CpuRegister.Rdx];
        var requiredSizeAddress = context[CpuRegister.Rcx];
        var poolCapacity = context[CpuRegister.R8];
        if (sourceAddress == 0)
        {
            return context.SetReturn(HttpErrorInvalidUrl);
        }

        if (outputAddress == 0 && poolAddress == 0 && requiredSizeAddress == 0)
        {
            return context.SetReturn(HttpErrorInvalidValue);
        }

        var readResult = ReadUriSource(context.Memory, sourceAddress, out var source);
        if (readResult != 0)
        {
            return context.SetReturn(readResult);
        }

        if (!TrySplitUri(source, out var components, out var isOpaque, out var port))
        {
            return context.SetReturn(HttpErrorInvalidUrl);
        }

        var requiredSize = 0;
        foreach (var component in components)
        {
            if (component is not null)
            {
                requiredSize += component.Length + 1;
            }
        }

        if (requiredSizeAddress != 0)
        {
            Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)requiredSize);
            if (!TryWriteUriBytes(context.Memory, requiredSizeAddress, sizeBytes))
            {
                return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        var output = new HttpUriComponents { IsOpaque = isOpaque ? 1 : 0, Port = port };
        if (outputAddress != 0 && poolAddress != 0)
        {
            if (poolCapacity < (ulong)requiredSize)
            {
                return context.SetReturn(HttpErrorOutOfMemory);
            }

            if (poolAddress > ulong.MaxValue - (ulong)(requiredSize - 1))
            {
                return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var poolBytes = new byte[requiredSize];
            Span<ulong> componentAddresses = stackalloc ulong[7];
            componentAddresses.Clear();
            var poolOffset = 0;
            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                var component = components[componentIndex];
                if (component is null)
                {
                    continue;
                }

                componentAddresses[componentIndex] = poolAddress + (ulong)poolOffset;
                Encoding.Latin1.GetBytes(component, poolBytes.AsSpan(poolOffset));
                poolOffset += component.Length + 1;
            }

            if (!TryWriteUriBytes(context.Memory, poolAddress, poolBytes))
            {
                return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            output.SchemeAddress = componentAddresses[0];
            output.UserNameAddress = componentAddresses[1];
            output.PasswordAddress = componentAddresses[2];
            output.HostNameAddress = componentAddresses[3];
            output.PathAddress = componentAddresses[4];
            output.QueryAddress = componentAddresses[5];
            output.FragmentAddress = componentAddresses[6];
        }

        if (outputAddress != 0 && !TryWriteUriBytes(
                context.Memory, outputAddress, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref output, 1))))
        {
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return context.SetReturn(0);
    }

    private static bool TryWriteUriBytes(ICpuMemory memory, ulong address, ReadOnlySpan<byte> bytes) =>
        address <= ulong.MaxValue - (ulong)(bytes.Length - 1) && memory.TryWrite(address, bytes);

    private static int ReadUriSource(ICpuMemory memory, ulong address, out string source)
    {
        source = string.Empty;
        var bytes = new byte[MaximumUriLength + 1];
        for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            if (address > ulong.MaxValue - (ulong)byteIndex ||
                !memory.TryRead(address + (ulong)byteIndex, bytes.AsSpan(byteIndex, 1)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (bytes[byteIndex] == 0)
            {
                // Keep each source byte unchanged, including escaped and non-ASCII data.
                source = Encoding.Latin1.GetString(bytes, 0, byteIndex);
                return 0;
            }
        }

        return HttpErrorInvalidUrl;
    }

    private static bool TrySplitUri(string source, out string?[] components, out bool isOpaque, out ushort port)
    {
        components = new string?[7];
        isOpaque = true;
        port = 0;
        if (source.Length == 0)
        {
            components[0] = components[3] = components[4] = string.Empty;
            return true;
        }

        var schemeEnd = source.IndexOf(':');
        if (schemeEnd <= 0 || !char.IsAsciiLetter(source[0]))
        {
            return false;
        }

        for (var characterIndex = 1; characterIndex < schemeEnd; characterIndex++)
        {
            var character = source[characterIndex];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        components[0] = source[..schemeEnd];
        var position = schemeEnd + 1;
        if (source.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
        {
            isOpaque = false;
            position += 2;
            var authorityEnd = position;
            while (authorityEnd < source.Length && source[authorityEnd] is not ('/' or '?' or '#'))
            {
                authorityEnd++;
            }

            var authority = source[position..authorityEnd];
            var credentialsEnd = authority.LastIndexOf('@');
            if (credentialsEnd >= 0)
            {
                var credentials = authority[..credentialsEnd];
                var passwordStart = credentials.IndexOf(':');
                components[1] = passwordStart < 0 ? credentials : credentials[..passwordStart];
                components[2] = passwordStart < 0 ? null : credentials[(passwordStart + 1)..];
                authority = authority[(credentialsEnd + 1)..];
            }

            var hostEnd = authority.Length;
            var portStart = -1;
            if (authority.StartsWith('['))
            {
                var bracketEnd = authority.IndexOf(']');
                if (bracketEnd < 0)
                {
                    return false;
                }

                hostEnd = bracketEnd + 1;
                if (hostEnd < authority.Length)
                {
                    if (authority[hostEnd] != ':')
                    {
                        return false;
                    }

                    portStart = hostEnd + 1;
                }
            }
            else
            {
                var portSeparator = authority.IndexOf(':');
                if (portSeparator >= 0)
                {
                    hostEnd = portSeparator;
                    portStart = portSeparator + 1;
                }
            }

            if (hostEnd > 0)
            {
                components[3] = authority[..hostEnd];
            }

            if (portStart >= 0)
            {
                if (portStart == authority.Length)
                {
                    return false;
                }

                var portValue = 0;
                foreach (var character in authority.AsSpan(portStart))
                {
                    if (!char.IsAsciiDigit(character))
                    {
                        return false;
                    }

                    portValue = portValue * 10 + character - '0';
                    if (portValue > ushort.MaxValue)
                    {
                        return false;
                    }
                }

                port = (ushort)portValue;
            }

            position = authorityEnd;
        }

        var pathStart = position;
        while (position < source.Length && source[position] is not ('?' or '#'))
        {
            position++;
        }

        if (position > pathStart)
        {
            components[4] = source[pathStart..position];
        }

        if (position < source.Length && source[position] == '?')
        {
            var queryStart = position++;
            while (position < source.Length && source[position] != '#')
            {
                position++;
            }

            components[5] = source[queryStart..position];
        }

        if (position < source.Length)
        {
            components[6] = source[position..];
        }

        return true;
    }
}
