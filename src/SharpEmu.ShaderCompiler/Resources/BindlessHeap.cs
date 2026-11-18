// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The descriptor heaps a bindless shader indexes at run time: buffers of records that hold 32-byte T#s or 16-byte S#s,
// each read at a record only the shader knows. Descriptor set 1 holds one array per supported image class and two sampler
// arrays (plain and depth compare), and a bitset per array in which the shader marks every element it reads, so that the
// host can give that element a view. A program may read several heaps, and a record may hold more than a descriptor
// (a material or terrain record with its textures among other fields), so the elements a read takes are those of its
// heap view (HeapView): the host gives each view its own run of elements for every draw.
public static class BindlessHeap
{
    public const uint DescriptorSet = 1;
    // Every view of a draw has its own elements: a title that keeps two copies of a 46158-record texture heap beside its
    // tables needs more than twice that.
    public const uint Capacity = 131072;
    public const uint ImageRecordBytes = 32;
    // A T# an R128 image instruction reads is its first four words.
    public const uint R128RecordBytes = 16;
    public const uint SamplerRecordBytes = 16;
    public const uint FeedbackBinding = 63;
    public const uint FeedbackWordsPerArray = Capacity / 32;

    // After the mark bitsets the feedback buffer holds the numeric class of every image element (ClassCode, ClassBits
    // each), which the host writes with the element's view: the format of a record is unknown until the shader picks it.
    public const uint ClassBits = 2;
    public const uint ClassMask = (1u << (int)ClassBits) - 1;
    public const uint ClassesPerWord = 32 / ClassBits;
    public const uint ClassWordShift = 4;
    public const uint ClassWords = Capacity >> (int)ClassWordShift;

    // Set-1 elements from CallRecordFirstIndex on hold the views and samplers of call records (CallRecordSelector), which the
    // host assigns by their descriptor words; guest heap records use the elements below. CallRecordNullIndex stays null.
    public const uint CallRecordFirstIndex = Capacity - 16384;
    public const uint CallRecordNullIndex = Capacity - 1;

    // The call records buffer of a dispatch (DescriptorBindingKind.CallRecords): word 0 holds the row count N, then N rows of
    // CallRecordKeyWords (argument low, argument high, callee slot) in ascending order, then each row's first field word,
    // then the rows' fields: one set-1 element for each field of the row's callee (CallRecordCallee.Fields).
    public const uint CallRecordKeyWords = 3;

    // The lookup reaches this many rows; the host gives a dispatch no more.
    public const uint CallRecordMaximumRows = 32767;

    // The buffer holds at least this many words, so the lookup of an empty table reads inside it.
    public const uint CallRecordMinimumWords = 8;

    // The image classes set 1 declares, in binding order; the sampler arrays follow them. An array holds float views and,
    // when its class has integer forms (HoldsIntegers), the views of integer textures, which a shader reads through an
    // unsigned or a signed declaration of the same binding: an element's numeric class says which declaration reads it.
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

    public static uint MarkWords => (uint)ArrayCount * FeedbackWordsPerArray;

    public static uint FeedbackWords => MarkWords + ClassWords;

    // An integer texture is read by loads, which no cube or depth-compare class serves.
    public static bool HoldsIntegers(int slot) =>
        (uint)slot < (uint)ImageClasses.Length && ImageClasses[slot] is { Cube: false, Compare: false };

    public static uint ClassCode(ImageNumericClass numericClass) => numericClass switch
    {
        ImageNumericClass.Uint => 1,
        ImageNumericClass.Sint => 2,
        _ => 0,
    };

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
// size. A cube image is sampled through a cube view. An R128 image instruction reads a 16-byte T#: the words after it in
// the heap are not the descriptor's.
public sealed record HeapRecordSelector(uint HeapSource, int KeyMemoryIndex, uint Immediate, uint RecordBytes)
{
    public bool Cube { get; init; }

    public bool R128 { get; init; }
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

// A heap view of a program: the heap V# the plan evaluates for every draw, whether its records hold samplers, the
// immediate offset the program reads them at, and whether it reads them as 16-byte T#s (R128).
public sealed record HeapRecordSource(uint Source, bool Sampler, uint Immediate = 0, bool R128 = false);

// One heap view of a draw: the heap V#'s base address, record count and stride, the immediate offset of the view's reads,
// and whether its descriptors are 16-byte T#s.
public readonly record struct BindlessHeapDescriptor(bool Sampler, ulong Base, uint Records, uint Stride, uint Immediate = 0, bool R128 = false)
{
    // The size of the view's descriptor in a record.
    public uint DescriptorBytes =>
        Sampler ? BindlessHeap.SamplerRecordBytes : R128 ? BindlessHeap.R128RecordBytes : BindlessHeap.ImageRecordBytes;

    public static BindlessHeapDescriptor FromWords(bool sampler, ReadOnlySpan<uint> words, uint immediate = 0, bool r128 = false) =>
        new(sampler, words[0] | ((ulong)(words[1] & 0xFFFF) << 32), words[2], (words[1] >> 16) & 0x3FFF, immediate, r128);

    // The heap as its view sees it, which keys the view's run of elements (BindlessHeapViewTable). Packed descriptors
    // (a stride of the descriptor's size or less) are one view at whatever immediate the shader reads them, since the
    // read's whole offset picks the descriptor; their count is the V#'s bytes over the descriptor size (a raw V#, stride 0,
    // counts bytes, a strided one records of its stride). A record view is keyed by the descriptor's offset in a record.
    public BindlessHeapDescriptor ViewKey
    {
        get
        {
            if (Stride > DescriptorBytes)
            {
                return this with { Immediate = Immediate % Stride };
            }

            var bytes = Stride == 0 ? Records : (ulong)Records * Stride;
            return this with { Records = (uint)Math.Min(bytes / DescriptorBytes, uint.MaxValue), Immediate = 0 };
        }
    }
}

// How the shader reads one heap record: the key read whose dynamic offset it kept, the immediate offset, the descriptor
// size and the program's heap view (its number among ShaderResourcePlan.HeapRecordSources).
public readonly record struct HeapRecordUse(int KeyMemoryIndex, uint Immediate, uint RecordBytes, int View = 0);

// Where the elements of one heap view are for one draw, as the shader reads it from two dwords of shader data
// (BindingLayout.HeapViewsDword): the view's first element of set 1, the heap's record stride, the offset of the view's
// descriptor in a record, and how many records the view has elements for. The read at byte offset (key + immediate) of
// the heap takes element First + offset / Stride when offset % Stride is FieldOffset and that record is below Records;
// any other read takes the null element.
public readonly record struct HeapView(uint First, uint Stride, uint FieldOffset, uint Records)
{
    public const uint DwordCount = 2;

    // The stride and field offset are V# strides (14 bits); the first element and the count are below BindlessHeap.Capacity
    // (18 bits).
    public const uint StrideMask = 0x3FFF;
    public const int ElementBits = 18;
    public const uint ElementMask = (1u << ElementBits) - 1;

    // No element: every read of the view takes the null element.
    public static HeapView None => new(0, 1, 0, 0);

    // The records of a heap V# are its elements when they can hold more than the descriptor: the shader's key picks the
    // record and its immediate the field. A V# whose stride is the descriptor's size or less (a byte buffer, say) is
    // read as packed descriptors, whichever part of the offset the shader keeps in its key.
    public static HeapView Of(in BindlessHeapDescriptor heap, uint first, uint records)
    {
        var descriptorBytes = heap.DescriptorBytes;
        return heap.Stride <= descriptorBytes
            ? new HeapView(first, descriptorBytes, 0, records)
            : new HeapView(first, heap.Stride, heap.Immediate % heap.Stride, records);
    }

    // The guest address of the descriptor a record of the heap holds for this view.
    public ulong DescriptorAddress(in BindlessHeapDescriptor heap, uint record) => heap.Base + (ulong)record * Stride + FieldOffset;

    public uint Word0 => (First & ElementMask) | ((Stride & StrideMask) << ElementBits);

    public uint Word1 => (Records & ElementMask) | ((FieldOffset & StrideMask) << ElementBits);
}
