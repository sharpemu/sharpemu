// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Where a guest V#'s dwords come from. A static descriptor is evaluated ahead of
// dispatch into the dense buffer table; a runtime descriptor is fetched by the
// shader itself from guest memory (a descriptor array indexed at run time).
public enum BufferDescriptorProvenance : byte
{
    Static,
    Runtime,
}

// How the Vulkan backend lowers a guest buffer descriptor. This is backend
// policy, deliberately kept out of the guest descriptor representation: the
// same guest V# could be lowered differently depending on the access, the
// device capabilities or cost, without changing what the guest descriptor is.
public enum BufferLoweringStrategy : byte
{
    // A compile-time V# bound as a native storage-buffer descriptor.
    NativeBinding,

    // A runtime V# addressed through the physical-storage-buffer page table.
    // Base, stride and byte bounds are read from the descriptor's raw words at
    // run time, so an untyped/raw access needs no compile-time value at all.
    PhysicalStorageBuffer,

    // A runtime V# enumerated into a bounded set of native candidate bindings
    // and selected with an OpSwitch. Required when the access's format/type
    // cannot be reconstructed from the raw words alone.
    BoundedCandidateTable,
}

// The guest buffer view (V#) as the hardware sees it, independent of how the
// host binds it. Static descriptors reference a dense buffer already
// materialised by the host; runtime descriptors are loaded by the shader.
public sealed class GuestBufferDescriptor
{
    public BufferDescriptorProvenance Provenance { get; init; }

    // A static descriptor's index in ShaderResourceInfo.Buffers.
    public uint StaticResource { get; init; } = DescriptorConstants.NoIndex;

    // Chooses the backend lowering for one access through this descriptor.
    // A static descriptor always binds natively. A runtime descriptor is
    // addressed through the page table when the access is a raw dword/subword
    // access, and otherwise needs candidate enumeration because Vulkan fixes
    // the resource's format/type at pipeline creation.
    public BufferLoweringStrategy ChooseStrategy(bool typed, bool formatted, bool atomic) =>
        Provenance == BufferDescriptorProvenance.Static
            ? BufferLoweringStrategy.NativeBinding
            : typed || formatted || atomic
                ? BufferLoweringStrategy.BoundedCandidateTable
                : BufferLoweringStrategy.PhysicalStorageBuffer;
}
