// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The descriptor heaps a bindless shader indexes at run time: a texture heap of 32-byte T# records and a sampler heap of
// 16-byte S# records, each read at a record only the shader knows. Descriptor set 1 holds one array per supported image
// class and two sampler arrays (plain and depth compare), indexed by the record number, and a bitset per array in which
// the shader marks every record it reads, so that the host can give that record a view.
public static class BindlessHeap
{
    public const uint DescriptorSet = 1;
    public const uint Capacity = 65536;
    public const uint ImageRecordBytes = 32;
    public const uint SamplerRecordBytes = 16;
    public const uint FeedbackBinding = 63;
    public const uint FeedbackWordsPerArray = Capacity / 32;

    // Set-1 elements from CallRecordFirstIndex on hold the views and samplers of call records (CallRecordSelector), which the
    // host assigns by their descriptor words; guest heap records use the elements below. CallRecordNullIndex stays null.
    public const uint CallRecordFirstIndex = 49152;
    public const uint CallRecordNullIndex = Capacity - 1;

    // The call records buffer of a dispatch (DescriptorBindingKind.CallRecords): word 0 holds the row count N, then N rows of
    // CallRecordKeyWords (argument low, argument high, callee slot) in ascending order, then each row's first field word,
    // then the rows' fields: one set-1 element for each field of the row's callee (CallRecordCallee.Fields).
    public const uint CallRecordKeyWords = 3;

    // The lookup reaches this many rows; the host gives a dispatch no more.
    public const uint CallRecordMaximumRows = 32767;

    // The buffer holds at least this many words, so the lookup of an empty table reads inside it.
    public const uint CallRecordMinimumWords = 8;

    // The image classes set 1 declares, in binding order; the sampler arrays follow them. A heap image is sampled as
    // float, since its format is unknown until the shader picks its record.
    public static readonly HeapImageClass[] ImageClasses = BuildImageClasses();

    public static readonly DescriptorBindingKind[] ImageKinds = ImageClasses.Select(static heapClass => heapClass.Kind).ToArray();

    // A 3D read of a record that holds a 2D texture reads it through the float 2D-array class: the hardware takes the
    // third coordinate as the layer.
    public static readonly int VolumeSlot = Array.FindIndex(ImageClasses, static heapClass => heapClass.Dimension == ImageDimension.Dim3D);

    public static readonly int VolumeLayerSlot = Array.FindIndex(ImageClasses, static heapClass =>
        heapClass is { Dimension: ImageDimension.Dim2DArray, Cube: false, Compare: false });

    public static int SamplerSlot => ImageKinds.Length;

    public static int CompareSamplerSlot => ImageKinds.Length + 1;

    public static int ArrayCount => ImageKinds.Length + 2;

    public static uint FeedbackWords => (uint)ArrayCount * FeedbackWordsPerArray;

    // The binding of an image class in set 1, or -1 when set 1 has no array for it.
    public static int ImageSlot(DescriptorBindingKind kind) => Array.IndexOf(ImageKinds, kind);

    private static HeapImageClass[] BuildImageClasses()
    {
        static HeapImageClass Class(ImageDimension dimension, bool cube = false, bool compare = false) =>
            new(ImageDescriptorBinding.ForImage(new ImageResource
            {
                ResourceClass = ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float,
                Dimension = dimension,
                Cube = cube,
                DepthCompare = compare,
            }) ?? throw new InvalidOperationException("A bindless heap image class has no binding kind."), dimension, cube, compare);

        return
        [
            Class(ImageDimension.Dim2D),
            Class(ImageDimension.Dim2DArray),
            Class(ImageDimension.Dim3D),
            Class(ImageDimension.Dim1D),
            Class(ImageDimension.Dim2DArray, cube: true),
            Class(ImageDimension.Dim2D, compare: true),
            Class(ImageDimension.Dim2DArray, compare: true),
            Class(ImageDimension.Dim2DArray, cube: true, compare: true),
        ];
    }
}

// One image array of set 1: its binding kind and the view it holds.
public readonly record struct HeapImageClass(DescriptorBindingKind Kind, ImageDimension Dimension, bool Cube, bool Compare);

// A descriptor the shader reads from a bindless heap at a record it picks at run time: the heap V# source, the scalar
// read of the record's first dword (whose dynamic offset the shader keeps), that read's immediate offset and the record
// size. A cube image is sampled through a cube view.
public sealed record HeapRecordSelector(uint HeapSource, int KeyMemoryIndex, uint Immediate, uint RecordBytes)
{
    public bool Cube { get; init; }
}

// One image access whose image or sampler is a heap record.
public sealed record HeapRecordAccess(int MemoryIndex, uint Source, bool Sampler);

// An image or sampler a linked callee reads through its record's run-time argument (ShaderCallEntryControl.RuntimeArgument),
// Immediate bytes past it. Every record of the callee has its own; the host resolves them into set 1 for the records of each
// dispatch. A cube image is sampled through a cube view.
public sealed record CallRecordSelector(uint Immediate, uint RecordBytes)
{
    public bool Cube { get; init; }
}

// One image access whose image or sampler a call record holds.
public sealed record CallRecordAccess(int MemoryIndex, uint Source, bool Sampler);

// A descriptor every record of one callee holds: its offset from the record's argument, whether it is a sampler, and the
// set-1 array it is read from.
public readonly record struct CallRecordField(uint Immediate, bool Sampler, int Slot);

// Where an access reads its call record descriptor: the field's number among its callee's fields, and the set-1 array.
public readonly record struct CallRecordFieldUse(int Field, int Slot);

// A buffer V# every record of one callee holds Immediate bytes past its argument, and whether the callee writes the buffer.
public readonly record struct CallRecordBuffer(uint Immediate, bool Written);

// What the plan finds a callee reads through each record's argument besides images and samplers: the buffers whose V#s the
// record holds, those whose V#s the callee computes from it (CallRecordBufferWords), and how many bytes past the argument its
// own scalar reads reach.
public sealed record CallRecordCalleePlan(CallRecordBuffer[] Buffers, CallRecordBufferWords[] BufferWords, uint ArgumentBytes);

// A buffer V# a callee computes from its record's argument (a V# read from memory a V# of the record names, say): its words
// with the argument as the call argument pair (ScalarValueGraph.CallArgumentRegisterBase), and whether the callee writes it.
public sealed record CallRecordBufferWords(ScalarValue[] Words, bool Written);

// What the host resolves for each record of one callee: its image and sampler fields in the order the module numbers them,
// its buffers, which the host makes resident through the device-address table, and the bytes past the argument the callee
// reads itself.
public sealed record CallRecordCallee(CallRecordField[] Fields, CallRecordBuffer[] Buffers, uint ArgumentBytes);

// A heap V# the plan evaluates for every draw, and whether it holds samplers.
public sealed record HeapRecordSource(uint Source, bool Sampler);

// One heap V# a draw's bindless accesses read: its base address, record count and stride.
public readonly record struct BindlessHeapDescriptor(bool Sampler, ulong Base, uint Records, uint Stride)
{
    public static BindlessHeapDescriptor FromWords(bool sampler, ReadOnlySpan<uint> words) =>
        new(sampler, words[0] | ((ulong)(words[1] & 0xFFFF) << 32), words[2], (words[1] >> 16) & 0x3FFF);
}

// How the shader reads one heap record: the key read whose dynamic offset it kept, the immediate offset and the record size.
public readonly record struct HeapRecordUse(int KeyMemoryIndex, uint Immediate, uint RecordBytes);
