// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Metal;

// One argument buffer per stage, filled field by field in the layout's order, bound at slot 0;
// the push block at slot 1. Every referenced buffer and texture is made resident for the encoder.
internal static partial class MetalVideoPresenter
{
    private const nuint ResourceUsageRead = 1;
    private const nuint ResourceUsageWrite = 2;
    private const nuint RenderStageVertex = 1;
    private const nuint RenderStageFragment = 2;
    private const int AddressRangeEntryBytes = 24;
    private const int MinimumUploadBytes = 8;

    // The arena slice of every guest buffer of one draw and the device address the shader reads it at.
    private sealed class UploadedDrawBuffers
    {
        public nint[] Buffers = [];
        public ulong[] DeviceAddresses = [];
        public uint[] Lengths = [];
    }

    // Which encoder call binds a stage's buffers; zero render stages means a compute encoder.
    private readonly record struct StageEncoder(nint Encoder, nint SetBufferSelector, nuint RenderStages);

    private static UploadedDrawBuffers UploadDrawBuffers(
        nint device,
        GuestMemoryBuffer[] guests,
        List<(nint Pointer, GuestMemoryBuffer Guest)> writeBackBuffers)
    {
        var uploads = new UploadedDrawBuffers
        {
            Buffers = new nint[guests.Length],
            DeviceAddresses = new ulong[guests.Length],
            Lengths = new uint[guests.Length],
        };
        var selGpuAddress = MetalNative.Selector("gpuAddress");
        for (var index = 0; index < guests.Length; index++)
        {
            var guest = guests[index];
            var length = Math.Clamp(guest.Length, 0, guest.Data.Length);
            var slice = AllocateUpload(device, Math.Max(length, MinimumUploadBytes), out var buffer, out var offset);
            slice.Clear();
            guest.Data.AsSpan(0, length).CopyTo(slice);
            uploads.Buffers[index] = buffer;
            uploads.DeviceAddresses[index] = MetalNative.SendGpuAddress(buffer, selGpuAddress) + (ulong)offset;
            uploads.Lengths[index] = (uint)length;
            if (guest.Writable && guest.WriteBackToGuest)
            {
                unsafe
                {
                    fixed (byte* data = slice)
                    {
                        writeBackBuffers.Add(((nint)data, guest));
                    }
                }
            }
        }

        return uploads;
    }

    // The 32-dword push block of a draw: each stage's block sits at its own start dword.
    private static byte[]? MergePushData(GuestStageBindings[] stages)
    {
        byte[]? merged = null;
        foreach (var stage in stages)
        {
            if (stage.PushData is not { } words)
            {
                continue;
            }

            merged ??= new byte[PushData.ByteSize];
            var count = Math.Min(words.Length, (int)PushData.DwordCount);
            for (var index = 0; index < count; index++)
            {
                if (words[index] != 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(merged.AsSpan(index * sizeof(uint)), words[index]);
                }
            }
        }

        return merged;
    }

    private static ulong UploadDwords(nint device, uint[] words, out nint buffer)
    {
        var slice = AllocateUpload(device, Math.Max(words.Length * sizeof(uint), MinimumUploadBytes), out buffer, out var offset);
        slice.Clear();
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(slice[(index * sizeof(uint))..], words[index]);
        }

        return MetalNative.SendGpuAddress(buffer, MetalNative.Selector("gpuAddress")) + (ulong)offset;
    }

    // The sorted range table the shader binary-searches: first page, page count and the device base of each.
    private static ulong UploadAddressRanges(nint device, GuestAddressRange[] ranges, UploadedDrawBuffers uploads, HashSet<nint> used, out nint buffer)
    {
        var slice = AllocateUpload(device, Math.Max(ranges.Length * AddressRangeEntryBytes, MinimumUploadBytes), out buffer, out var offset);
        slice.Clear();
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.BufferIndex < 0 || range.BufferIndex >= uploads.Buffers.Length)
            {
                throw new InvalidOperationException($"A device-address range names an unknown buffer: range={index} buffer={range.BufferIndex} buffers={uploads.Buffers.Length}.");
            }

            var entry = slice[(index * AddressRangeEntryBytes)..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, range.FirstPage);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], range.PageCount);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], uploads.DeviceAddresses[range.BufferIndex]);
            used.Add(uploads.Buffers[range.BufferIndex]);
        }

        return MetalNative.SendGpuAddress(buffer, MetalNative.Selector("gpuAddress")) + (ulong)offset;
    }

    private static void EncodeStageResources(
        nint device,
        in StageEncoder encoder,
        Gen5MslArgumentLayout? layout,
        GuestStageBindings bindings,
        UploadedDrawBuffers uploads,
        ReadOnlySpan<nint> textureHandles,
        byte[]? pushData)
    {
        var used = new HashSet<nint>();
        var selResourceId = MetalNative.Selector("gpuResourceID");
        var selGpuAddress = MetalNative.Selector("gpuAddress");
        if (layout is not null && layout.Fields.Count != 0)
        {
            var argument = AllocateUpload(device, (int)Math.Max(layout.ByteSize, MinimumUploadBytes), out var argumentBuffer, out var argumentOffset);
            argument.Clear();
            var elementCursor = 0;
            foreach (var field in layout.Fields)
            {
                var span = argument.Slice((int)field.ByteOffset, (int)field.ByteSize);
                switch (field.FieldKind)
                {
                    case MslArgumentFieldKind.BufferPointers:
                    case MslArgumentFieldKind.BufferByteCounts:
                        if (bindings.BufferIndices.Length != field.Count)
                        {
                            throw new InvalidOperationException($"The stage buffers do not match the layout: stage={bindings.Stage} buffers={bindings.BufferIndices.Length} layout={field.Count} hash=0x{bindings.ProgramHash:X16}.");
                        }

                        for (var index = 0; index < field.Count; index++)
                        {
                            var bufferIndex = bindings.BufferIndices[index];
                            if (bufferIndex < 0 || bufferIndex >= uploads.Buffers.Length)
                            {
                                throw new InvalidOperationException($"A stage buffer index is outside the draw: stage={bindings.Stage} buffer={bufferIndex} buffers={uploads.Buffers.Length} hash=0x{bindings.ProgramHash:X16}.");
                            }

                            if (field.FieldKind == MslArgumentFieldKind.BufferPointers)
                            {
                                BinaryPrimitives.WriteUInt64LittleEndian(span[(index * sizeof(ulong))..], uploads.DeviceAddresses[bufferIndex]);
                                used.Add(uploads.Buffers[bufferIndex]);
                            }
                            else
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(span[(index * sizeof(uint))..], uploads.Lengths[bufferIndex]);
                            }
                        }

                        break;
                    case MslArgumentFieldKind.Textures:
                        if (elementCursor + field.Count > bindings.ImageElements.Length)
                        {
                            throw new InvalidOperationException($"The stage images do not match the layout: stage={bindings.Stage} elements={bindings.ImageElements.Length} needed={elementCursor + field.Count} hash=0x{bindings.ProgramHash:X16}.");
                        }

                        for (var index = 0; index < field.Count; index++)
                        {
                            var textureIndex = bindings.ImageElements[elementCursor + index];
                            if (textureIndex < 0 || textureIndex >= textureHandles.Length)
                            {
                                throw new InvalidOperationException($"A stage image index is outside the draw: stage={bindings.Stage} image={textureIndex} images={textureHandles.Length} hash=0x{bindings.ProgramHash:X16}.");
                            }

                            var texture = textureHandles[textureIndex];
                            if (texture != 0)
                            {
                                BinaryPrimitives.WriteUInt64LittleEndian(span[(index * sizeof(ulong))..], MetalNative.SendGpuResourceId(texture, selResourceId));
                                used.Add(texture);
                            }
                        }

                        elementCursor += (int)field.Count;
                        break;
                    case MslArgumentFieldKind.Samplers:
                        if (bindings.Samplers.Length != field.Count)
                        {
                            throw new InvalidOperationException($"The stage samplers do not match the layout: stage={bindings.Stage} samplers={bindings.Samplers.Length} layout={field.Count} hash=0x{bindings.ProgramHash:X16}.");
                        }

                        for (var index = 0; index < field.Count; index++)
                        {
                            var sampler = GetOrCreateSampler(device, bindings.Samplers[index]);
                            BinaryPrimitives.WriteUInt64LittleEndian(span[(index * sizeof(ulong))..], MetalNative.SendGpuResourceId(sampler, selResourceId));
                        }

                        break;
                    case MslArgumentFieldKind.Pointer:
                        BinaryPrimitives.WriteUInt64LittleEndian(span, PointerFieldValue(device, field.Kind, bindings, uploads, used, selGpuAddress));
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(span, WordFieldValue(field.Kind, bindings));
                        break;
                }
            }

            MetalNative.SendSetBuffer(encoder.Encoder, encoder.SetBufferSelector, argumentBuffer, (nuint)argumentOffset, Gen5MslArgumentLayout.ResourcesBufferIndex);
        }

        if (bindings.PushData is not null && pushData is not null)
        {
            var slice = AllocateUpload(device, pushData.Length, out var pushBuffer, out var pushOffset);
            pushData.CopyTo(slice);
            MetalNative.SendSetBuffer(encoder.Encoder, encoder.SetBufferSelector, pushBuffer, (nuint)pushOffset, Gen5MslArgumentLayout.PushDataBufferIndex);
        }

        UseResources(in encoder, used);
    }

    private static ulong PointerFieldValue(nint device, DescriptorBindingKind kind, GuestStageBindings bindings, UploadedDrawBuffers uploads, HashSet<nint> used, nint selGpuAddress)
    {
        nint buffer;
        switch (kind)
        {
            case DescriptorBindingKind.GlobalDataShare:
                buffer = EnsureGlobalDataShareBuffer(device);
                used.Add(buffer);
                return MetalNative.SendGpuAddress(buffer, selGpuAddress);
            case DescriptorBindingKind.DeviceAddressPageTable:
                var table = UploadAddressRanges(device, bindings.AddressRanges, uploads, used, out buffer);
                used.Add(buffer);
                return table;
            case DescriptorBindingKind.FaultBuffer:
                buffer = RecordFaultScan(device, bindings.ProgramHash);
                used.Add(buffer);
                return MetalNative.SendGpuAddress(buffer, selGpuAddress);
            case DescriptorBindingKind.FlattenedResourceTable:
                var flattened = UploadDwords(device, bindings.FlattenedTable, out buffer);
                used.Add(buffer);
                return flattened;
            case DescriptorBindingKind.ShaderData:
                var shaderData = UploadDwords(device, bindings.ShaderData, out buffer);
                used.Add(buffer);
                return shaderData;
            default:
                throw new InvalidOperationException($"The layout has a pointer field of an unknown kind: kind={kind} hash=0x{bindings.ProgramHash:X16}.");
        }
    }

    private static uint WordFieldValue(DescriptorBindingKind kind, GuestStageBindings bindings) => kind switch
    {
        DescriptorBindingKind.GlobalDataShare => ManagedCommandStreamHost.GdsBytes,
        DescriptorBindingKind.DeviceAddressPageTable => (uint)bindings.AddressRanges.Length,
        DescriptorBindingKind.FaultBuffer => (uint)FaultBitmapWords,
        DescriptorBindingKind.FlattenedResourceTable => (uint)bindings.FlattenedTable.Length,
        DescriptorBindingKind.ShaderData => (uint)bindings.ShaderData.Length,
        _ => throw new InvalidOperationException($"The layout has a word field of an unknown kind: kind={kind} hash=0x{bindings.ProgramHash:X16}."),
    };

    // Argument-buffer references are indirect; the encoder must be told which resources it touches.
    private static void UseResources(in StageEncoder encoder, HashSet<nint> resources)
    {
        if (resources.Count == 0)
        {
            return;
        }

        const nuint usage = ResourceUsageRead | ResourceUsageWrite;
        if (encoder.RenderStages == 0)
        {
            var selector = MetalNative.Selector("useResource:usage:");
            foreach (var resource in resources)
            {
                MetalNative.SendUseResourceUsage(encoder.Encoder, selector, resource, usage);
            }
        }
        else
        {
            var selector = MetalNative.Selector("useResource:usage:stages:");
            foreach (var resource in resources)
            {
                MetalNative.SendUseResource(encoder.Encoder, selector, resource, usage, encoder.RenderStages);
            }
        }
    }

    // A shader compiled against a layout needs that stage's bindings on every draw that uses it.
    private static GuestStageBindings? FindStageBindings(GuestStageBindings[] stages, GuestStageKind kind, MetalCompiledGuestShader? shader)
    {
        foreach (var stage in stages)
        {
            if (stage.Stage == kind)
            {
                return stage;
            }
        }

        if (shader?.Shader.ArgumentLayout is { } layout && layout.Fields.Count != 0)
        {
            throw new InvalidOperationException($"The {kind} stage has an argument layout but the draw carries no bindings for it: entry={shader.Shader.EntryPoint}.");
        }

        return null;
    }
}
