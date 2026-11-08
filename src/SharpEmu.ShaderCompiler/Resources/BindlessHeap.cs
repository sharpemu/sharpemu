// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Descriptor set 1: the images and samplers of call records (CallRecordSelector), which a program linked with run-time call
// records reads at elements the host assigns. It holds one array per supported image class and two sampler arrays (plain
// and depth compare).
public static class BindlessHeap
{
    public const uint DescriptorSet = 1;
    public const uint Capacity = 16384;

    // The host gives each distinct call record descriptor an element of its array, by the descriptor's words.
    // CallRecordNullIndex stays null.
    public const uint CallRecordNullIndex = Capacity - 1;

    // The call records buffer of a dispatch (DescriptorBindingKind.CallRecords): word 0 holds the row count N, then N rows of
    // CallRecordKeyWords (argument low, argument high, callee slot) in ascending order, then each row's first field word,
    // then the rows' fields: one set-1 element for each field of the row's callee (CallRecordCallee.Fields).
    public const uint CallRecordKeyWords = 3;

    // The lookup reaches this many rows; the host gives a dispatch no more.
    public const uint CallRecordMaximumRows = 32767;

    // The buffer holds at least this many words, so the lookup of an empty table reads inside it.
    public const uint CallRecordMinimumWords = 8;

    // The image classes set 1 declares, in binding order; the sampler arrays follow them. A record's image is sampled as
    // float, since its format is unknown until the shader finds its record.
    public static readonly HeapImageClass[] ImageClasses = BuildImageClasses();

    public static readonly DescriptorBindingKind[] ImageKinds = ImageClasses.Select(static heapClass => heapClass.Kind).ToArray();

    public static int SamplerSlot => ImageKinds.Length;

    public static int CompareSamplerSlot => ImageKinds.Length + 1;

    public static int ArrayCount => ImageKinds.Length + 2;

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
