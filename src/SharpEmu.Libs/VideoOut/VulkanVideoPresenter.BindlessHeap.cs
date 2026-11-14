// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Descriptor set 1: the bindless heaps a program reads at a record only its shader knows (see BindlessHeap). Every
// element starts as a null descriptor. The shader marks each record it reads in a feedback bitset; once the marking
// work completes, the host reads the record's T# or S# from the guest heap and writes its view or sampler. Records are
// read again periodically, and a heap image's contents and layout are made current before each command that reads
// set 1.
internal static unsafe partial class VulkanVideoPresenter
{
    // Revalidation reads the heap records it compares in runs, one guest read per run of nearby records, instead of one
    // read per record (SHARPEMU_HEAP_BULK_READS=0 reads each record on its own). A run that cannot be read falls back to
    // reading its records one by one, so every entry compares the words its own read would return.
    internal static readonly bool HeapBulkReadsConfigured = Environment.GetEnvironmentVariable("SHARPEMU_HEAP_BULK_READS") != "0";
    // Tests set it.
    internal static bool? HeapBulkReadsOverride;
    internal static bool HeapBulkReadsActive => HeapBulkReadsOverride ?? HeapBulkReadsConfigured;

    // A run spans at most this many bytes and skips at most HeapRunGapBytes between two records it reads.
    internal const uint HeapRunMaxBytes = 64 << 10;
    internal const uint HeapRunGapBytes = 1 << 10;

    // The end (exclusive) of the run of sorted record indexes that starts at start: every index in it is below records,
    // the run spans at most HeapRunMaxBytes, and no two neighbors are more than HeapRunGapBytes of records apart.
    internal static int HeapRecordRunEnd(ReadOnlySpan<uint> indexes, int start, uint recordBytes, uint records)
    {
        var first = indexes[start];
        var last = first;
        var end = start + 1;
        while (end < indexes.Length)
        {
            var next = indexes[end];
            if (next >= records || (ulong)(next - last) * recordBytes > HeapRunGapBytes + recordBytes ||
                ((ulong)next - first + 1) * recordBytes > HeapRunMaxBytes)
            {
                break;
            }

            last = next;
            end++;
        }

        return end;
    }

    // What revalidation in runs reads and visits. The presenter implements it over its heaps, guest memory and
    // entries (Presenter.HeapRunVisitor); tests implement it over fakes.
    internal interface IHeapRunVisitor
    {
        // The heap as it is now: a resolve may move it.
        BindlessHeapDescriptor Heap(bool sampler);

        bool TryRead(ulong address, Span<byte> destination);

        // The record of the entry at position, read on its own as ReadHeapRecord reads it; the word count.
        int ReadRecord(int position, Span<uint> words);

        // Touches and compares the entry at position, and resolves it again when its words changed; true when it did.
        bool Visit(int position, ReadOnlySpan<uint> words);
    }

    // Visits the entries at positions [from, to) of one heap, whose sorted record indexes are indexes, each with the
    // words of its record. Records are read in runs (HeapRecordRunEnd) into buffer. A visit that resolved ends its run:
    // the next entry starts a new one, read after that resolve, with the heap as the resolve left it. So no entry compares
    // bytes read before an earlier entry's resolve (which may land GPU data or move the heap), and buffer is never read
    // across one (a nested revalidation may reuse it). A run that cannot be read falls back to one read per entry, each
    // right before its visit. Entries the heap cannot hold get no words, as ReadHeapRecord gives them.
    internal static void VisitHeapRuns<T>(ref T visitor, ReadOnlySpan<uint> indexes, int from, int to, bool sampler,
        Span<byte> buffer, Span<uint> scratch) where T : IHeapRunVisitor
    {
        var bytes = sampler ? BindlessHeap.SamplerRecordBytes : BindlessHeap.ImageRecordBytes;
        indexes = indexes[..to];
        var start = from;
        while (start < to)
        {
            var heap = visitor.Heap(sampler);
            if (heap.Base == 0 || indexes[start] >= heap.Records)
            {
                _ = visitor.Visit(start, ReadOnlySpan<uint>.Empty);
                start++;
                continue;
            }

            var end = HeapRecordRunEnd(indexes, start, bytes, heap.Records);
            var first = indexes[start];
            var run = buffer[..checked((int)(((ulong)indexes[end - 1] - first + 1) * bytes))];
            if (!visitor.TryRead(heap.Base + (ulong)first * bytes, run))
            {
                for (; start < end; start++)
                {
                    _ = visitor.Visit(start, scratch[..visitor.ReadRecord(start, scratch)]);
                }

                continue;
            }

            while (start < end)
            {
                var offset = checked((int)((indexes[start] - first) * bytes));
                var resolved = visitor.Visit(start, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(run.Slice(offset, (int)bytes)));
                start++;
                if (resolved)
                {
                    break;
                }
            }
        }
    }

    private sealed partial class Presenter
    {
        private const DescriptorBindingFlags HeapElementFlags =
            DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.PartiallyBoundBit |
            DescriptorBindingFlags.UpdateUnusedWhilePendingBit;

        // Records are read again after this many submission ticks, so a rewritten record gets its new view.
        private const ulong HeapRevalidationTicks = 16;

        // Set 1 exists on every device that supports it: programs linked with run-time call records read their records
        // through it, and other programs read guest heap records through it.
        bool Gpu.Pipelines.IShaderPipelineHost.RuntimeCallRecordsSupported => _bindlessHeapSupported;

        private static ulong FeedbackBytes => (ulong)BindlessHeap.FeedbackWords * sizeof(uint);

        // One record the shaders marked: the words it was resolved from and what set 1 holds for it.
        private sealed class HeapEntry
        {
            public int Slot;
            public uint Index;
            public uint[] Words = [];
            public CachedImage? Image;
            public ResourceSlotIdentifier ImageIdentifier;
            public ImageRequest Request;
            public bool Resolved;
        }

        private bool _bindlessHeapSupported;
        private DescriptorSetLayout _heapSetLayout;
        private DescriptorPool _heapPool;
        private DescriptorSet _heapSet;
        private GpuBuffer? _heapFeedback;
        private GpuBuffer? _heapFeedbackReadback;
        private Action? _heapFeedbackCopyCompleted;
        private int _heapFeedbackReady;
        private int _heapFeedbackCopyPending;
        private uint[] _heapMarked = [];
        private readonly Dictionary<(int Slot, uint Index), HeapEntry> _heapEntries = new();
        private readonly Dictionary<CachedImage, List<HeapEntry>> _heapEntriesByImage = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<CachedImage> _heapChangedImages = new(ReferenceEqualityComparer.Instance);
        private BindlessHeapDescriptor _textureHeap;
        private BindlessHeapDescriptor _samplerHeap;
        private ulong _heapRevalidatedTick;
        private object? _heapPreparedFor;
        // Changes whenever _heapEntries does; the entries in record order (image heap, then sampler heap, each by
        // index), their indexes and where the sampler heap's begin, as of _heapRunVersion; and the run buffer.
        private long _heapEntriesVersion;
        private long _heapRunVersion;
        private HeapEntry[]? _heapRunOrder;
        private uint[]? _heapRunIndexes;
        private int _heapRunSamplerStart;
        private byte[]? _heapRunBytes;
        private Sampler _heapNullSampler;
        private Action<CachedImage>? _heapImageChanged;
        private int _heapReports;

        // Called during device setup with the queried features; the layout is made when the device supports the heap.
        private static bool BindlessHeapSupported(in PhysicalDeviceVulkan12Features features, bool nullDescriptor) =>
            nullDescriptor && features.DescriptorIndexing && features.RuntimeDescriptorArray &&
            features.ShaderSampledImageArrayNonUniformIndexing && features.DescriptorBindingSampledImageUpdateAfterBind &&
            features.DescriptorBindingPartiallyBound && features.DescriptorBindingUpdateUnusedWhilePending;

        private void CreateBindlessHeapLayout()
        {
            if (!_bindlessHeapSupported)
            {
                return;
            }

            var indexing = new PhysicalDeviceDescriptorIndexingProperties { SType = StructureType.PhysicalDeviceDescriptorIndexingProperties };
            var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &indexing };
            _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties);
            var images = (ulong)BindlessHeap.ImageKinds.Length * BindlessHeap.Capacity;
            var samplers = 2UL * BindlessHeap.Capacity;
            if (indexing.MaxDescriptorSetUpdateAfterBindSampledImages < images ||
                indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages < images ||
                indexing.MaxDescriptorSetUpdateAfterBindSamplers < samplers ||
                indexing.MaxPerStageDescriptorUpdateAfterBindSamplers < samplers ||
                indexing.MaxPerStageUpdateAfterBindResources < images + samplers + 1)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] The device cannot hold the bindless heap arrays: images={images} " +
                    $"max_images={indexing.MaxDescriptorSetUpdateAfterBindSampledImages} samplers={samplers} " +
                    $"max_samplers={indexing.MaxDescriptorSetUpdateAfterBindSamplers}; programs that read bindless heap records will fail.");
                _bindlessHeapSupported = false;
                return;
            }

            // Every stage a program that reads heap records can run in, the stages of a tessellated pipeline too.
            var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit |
                (_supportsMeshShader ? ShaderStageFlags.MeshBitExt : 0) |
                (_supportsTessellation ? ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit : 0);
            var count = BindlessHeap.ArrayCount + 1;
            var bindings = stackalloc DescriptorSetLayoutBinding[count];
            var flags = stackalloc DescriptorBindingFlags[count];
            for (var slot = 0; slot < BindlessHeap.ArrayCount; slot++)
            {
                bindings[slot] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)slot,
                    DescriptorType = slot < BindlessHeap.ImageKinds.Length ? DescriptorType.SampledImage : DescriptorType.Sampler,
                    DescriptorCount = BindlessHeap.Capacity,
                    StageFlags = stages,
                };
                flags[slot] = HeapElementFlags;
            }

            bindings[count - 1] = new DescriptorSetLayoutBinding
            {
                Binding = BindlessHeap.FeedbackBinding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = stages,
            };
            flags[count - 1] = 0;
            var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = (uint)count,
                PBindingFlags = flags,
            };
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = (uint)count,
                PBindings = bindings,
            };
            Check(_vk.CreateDescriptorSetLayout(_device, &create, null, out _heapSetLayout), "vkCreateDescriptorSetLayout(bindless heap)");
        }

        private DescriptorSetLayout RequireHeapSetLayout() =>
            _heapSetLayout.Handle != 0
                ? _heapSetLayout
                : throw SubmissionScheduler.Fatal(
                    "A program reads bindless heap records, but the device has no bindless heap set.");

        // The set, its null elements and the feedback bitsets, made at the first command that reads the heap.
        private void EnsureBindlessHeap()
        {
            if (_heapSet.Handle != 0)
            {
                return;
            }

            var layout = RequireHeapSetLayout();
            var sizes = stackalloc DescriptorPoolSize[3];
            sizes[0] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = (uint)BindlessHeap.ImageKinds.Length * BindlessHeap.Capacity };
            sizes[1] = new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = 2 * BindlessHeap.Capacity };
            sizes[2] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = 3,
                PPoolSizes = sizes,
            };
            Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _heapPool), "vkCreateDescriptorPool(bindless heap)");
            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _heapPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            DescriptorSet set;
            Check(_vk.AllocateDescriptorSets(_device, &allocate, &set), "vkAllocateDescriptorSets(bindless heap)");
            _heapSet = set;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Bindless heap: set={BindlessHeap.DescriptorSet} image_arrays={BindlessHeap.ImageKinds.Length} " +
                $"sampler_arrays=2 capacity={BindlessHeap.Capacity}");
            SetDebugName(ObjectType.DescriptorSet, set.Handle, "SharpEmu bindless heap");

            _heapFeedback = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, FeedbackBytes);
            _heapFeedbackReadback = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, FeedbackBytes);
            var completedReadback = _heapFeedbackReadback;
            _heapFeedbackCopyCompleted = () =>
            {
                // A callback from a retired heap must not make a replacement slot appear complete.
                if (ReferenceEquals(_heapFeedbackReadback, completedReadback))
                    Volatile.Write(ref _heapFeedbackReady, 1);
            };
            _ = BeginBatchedGuestCommands();
            _heapFeedback.Fill(0, FeedbackBytes, 0);
            _heapMarked = new uint[BindlessHeap.FeedbackWords];
            Span<uint> nullSampler = stackalloc uint[4];
            _heapNullSampler = _samplerStore.GetSampler(new SamplerDescriptorWords(nullSampler), integerView: false);
            _heapImageChanged = image =>
            {
                lock (_heapChangedImages)
                {
                    _heapChangedImages.Add(image);
                }
            };

            // Every element is a null view or the null sampler until its record is read.
            var nullImages = new DescriptorImageInfo[BindlessHeap.Capacity];
            Array.Fill(nullImages, new DescriptorImageInfo { ImageLayout = ImageLayout.General });
            var nullSamplers = new DescriptorImageInfo[BindlessHeap.Capacity];
            Array.Fill(nullSamplers, new DescriptorImageInfo { Sampler = _heapNullSampler });
            var feedback = new DescriptorBufferInfo { Buffer = _heapFeedback.Handle, Offset = 0, Range = FeedbackBytes };
            var writes = new WriteDescriptorSet[BindlessHeap.ArrayCount + 1];
            fixed (DescriptorImageInfo* images = nullImages)
            fixed (DescriptorImageInfo* samplers = nullSamplers)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                for (var slot = 0; slot < BindlessHeap.ArrayCount; slot++)
                {
                    var image = slot < BindlessHeap.ImageKinds.Length;
                    writes[slot] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = set,
                        DstBinding = (uint)slot,
                        DescriptorCount = BindlessHeap.Capacity,
                        DescriptorType = image ? DescriptorType.SampledImage : DescriptorType.Sampler,
                        PImageInfo = image ? images : samplers,
                    };
                }

                writes[BindlessHeap.ArrayCount] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = BindlessHeap.FeedbackBinding,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &feedback,
                };
                _vk.UpdateDescriptorSets(_device, (uint)writes.Length, writePointer, 0, null);
            }
        }

        private void DestroyBindlessHeap()
        {
            // The image cache disposes every slot after this, the ones the heap holds too.
            foreach (var image in _heapEntriesByImage.Keys)
            {
                image.HeapChanged = null;
                image.DeferredErase = null;
            }

            _heapEntriesByImage.Clear();
            _heapEntries.Clear();
            _heapEntriesVersion++;
            _callRecordEntries.Clear();
            Array.Clear(_callRecordNext);
            _heapFeedback?.Dispose();
            _heapFeedbackReadback?.Dispose();
            _heapFeedback = null;
            _heapFeedbackReadback = null;
            _heapFeedbackCopyCompleted = null;
            Volatile.Write(ref _heapFeedbackReady, 0);
            Volatile.Write(ref _heapFeedbackCopyPending, 0);
            if (_heapPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, _heapPool, null);
                _heapPool = default;
                _heapSet = default;
            }

            if (_heapSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, _heapSetLayout, null);
                _heapSetLayout = default;
            }
        }

        // The recording host reuses one preparation object for every draw, so the first stage of each draw that reads set 1
        // forgets the heap prepared for the previous one; otherwise the heap is prepared once and the records later draws
        // mark never get images.
        internal void ForgetHeapPreparation(object preparation)
        {
            if (ReferenceEquals(_heapPreparedFor, preparation))
            {
                _heapPreparedFor = null;
            }
        }

        // Before the images of a draw or dispatch that reads set 1 are found: the heap V#s, the records marked since the
        // last look, records rewritten since they were read, and heap images whose contents or layout changed.
        internal void PrepareBindlessHeap(object preparation, BindlessHeapDescriptor[] heaps)
        {
            if (ReferenceEquals(_heapPreparedFor, preparation))
            {
                return;
            }

            _heapPreparedFor = preparation;
            EnsureBindlessHeap();
            var revalidate = _scheduler.CurrentTick - _heapRevalidatedTick >= HeapRevalidationTicks;
            foreach (var heap in heaps)
            {
                ref var current = ref heap.Sampler ? ref _samplerHeap : ref _textureHeap;
                if (current == heap)
                {
                    continue;
                }

                if (current.Base != 0 && current.Base != heap.Base && _heapReports++ < 16)
                {
                    Console.Error.WriteLine(
                        $"[GPU][WARN] Bindless {(heap.Sampler ? "sampler" : "texture")} heap moved: from=0x{current.Base:X16} to=0x{heap.Base:X16} records={heap.Records}");
                }

                current = heap;
                revalidate = true;
            }

            PollHeapFeedback();
            if (revalidate)
            {
                RevalidateHeapEntries();
            }

            RefreshChangedHeapImages();
        }

        public void BindBindlessHeap(CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout)
        {
            var set = _heapSet;
            if (set.Handle == 0)
            {
                throw SubmissionScheduler.Fatal("A program reads bindless heap records before the heap set exists.");
            }

            _vk.CmdBindDescriptorSets(command, bindPoint, layout, BindlessHeap.DescriptorSet, 1, &set, 0, null);
        }

        // Takes the marks of the last completed copy and starts the next copy; one copy is in flight at a time.
        private void PollHeapFeedback()
        {
            if (Interlocked.Exchange(ref _heapFeedbackReady, 0) != 0)
            {
                var completed = _heapFeedbackReadback!;
                completed.Invalidate(0, FeedbackBytes);
                // Keep the slot pending through consumption, including any reentrant preparation. No later GPU copy
                // may overwrite these mapped words until every marked record has been resolved.
                ResolveMarkedRecords(MemoryMarshal.Cast<byte, uint>(completed.Mapped[..(int)FeedbackBytes]));
                Volatile.Write(ref _heapFeedbackCopyPending, 0);
            }

            if (Volatile.Read(ref _heapFeedbackCopyPending) != 0)
            {
                return;
            }

            var feedback = _heapFeedback!;
            var readback = _heapFeedbackReadback!;
            Volatile.Write(ref _heapFeedbackCopyPending, 1);
            _ = BeginBatchedGuestCommands();
            readback.CopyFrom(
                _scheduler.Current, feedback, 0, 0, FeedbackBytes,
                AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit, AccessFlags.None,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                AccessFlags.HostReadBit);
            _scheduler.QueueCompletionAction(_heapFeedbackCopyCompleted!);
        }

        private void ResolveMarkedRecords(ReadOnlySpan<uint> marked)
        {
            for (var word = 0; word < marked.Length && word < _heapMarked.Length; word++)
            {
                var fresh = marked[word] & ~_heapMarked[word];
                if (fresh == 0)
                {
                    continue;
                }

                _heapMarked[word] |= fresh;
                var slot = word / (int)BindlessHeap.FeedbackWordsPerArray;
                var first = (uint)(word % (int)BindlessHeap.FeedbackWordsPerArray) * 32;
                while (fresh != 0)
                {
                    var index = first + (uint)BitOperations.TrailingZeroCount(fresh);
                    fresh &= fresh - 1;
                    if (index >= BindlessHeap.CallRecordFirstIndex)
                    {
                        // Call records own these elements; the shader reads a heap record past the heap's range as null.
                        continue;
                    }

                    var entry = new HeapEntry { Slot = slot, Index = index };
                    _heapEntries[(slot, index)] = entry;
                    _heapEntriesVersion++;
                    entry.Words = ReadHeapRecord(slot, index);
                    ResolveHeapEntry(entry);
                }
            }
        }

        private void RevalidateHeapEntries()
        {
            _heapRevalidatedTick = _scheduler.CurrentTick;
            Span<uint> scratch = stackalloc uint[8];
            if (HeapBulkReadsActive)
            {
                RevalidateHeapEntriesInRuns(scratch);
            }
            else
            {
                foreach (var entry in _heapEntries.Values)
                {
                    RevalidateHeapEntry(entry, scratch[..ReadHeapRecord(entry.Slot, entry.Index, scratch)]);
                }
            }

            // A call record entry is named by its words, which never change; its image stays in use.
            foreach (var entry in _callRecordEntries.Values)
            {
                if (entry.Image is { } image && _imageCache.TryGetImage(entry.ImageIdentifier, out var found) && ReferenceEquals(found, image))
                {
                    _ = _imageCache.GetImage(entry.ImageIdentifier);
                }
            }
        }

        // True when the entry's words changed and it was resolved again.
        private bool RevalidateHeapEntry(HeapEntry entry, ReadOnlySpan<uint> words)
        {
            if (entry.Image is { } image && _imageCache.TryGetImage(entry.ImageIdentifier, out var found) && ReferenceEquals(found, image))
            {
                // Keeps the image the heap reads from the cache's collection of unused images.
                _ = _imageCache.GetImage(entry.ImageIdentifier);
            }

            if (words.SequenceEqual(entry.Words))
            {
                return false;
            }

            entry.Words = words.ToArray();
            ResolveHeapEntry(entry);
            return true;
        }

        // The presenter's side of VisitHeapRuns, over the entries in record order.
        private readonly struct HeapRunVisitor(Presenter owner, HeapEntry[] order) : IHeapRunVisitor
        {
            public BindlessHeapDescriptor Heap(bool sampler) => sampler ? owner._samplerHeap : owner._textureHeap;

            public bool TryRead(ulong address, Span<byte> destination) => owner._guestMemory.TryRead(address, destination);

            public int ReadRecord(int position, Span<uint> words) =>
                owner.ReadHeapRecord(order[position].Slot, order[position].Index, words);

            public bool Visit(int position, ReadOnlySpan<uint> words) => owner.RevalidateHeapEntry(order[position], words);
        }

        // Every entry, in record order, compared with words read in runs (HeapRecordRunEnd).
        private void RevalidateHeapEntriesInRuns(Span<uint> scratch)
        {
            if (_heapRunOrder is null || _heapRunIndexes is null || _heapRunVersion != _heapEntriesVersion)
            {
                var order = _heapEntries.Values.ToArray();
                Array.Sort(order, static (left, right) =>
                {
                    var heap = (left.Slot >= BindlessHeap.ImageKinds.Length).CompareTo(right.Slot >= BindlessHeap.ImageKinds.Length);
                    return heap != 0 ? heap : left.Index != right.Index ? left.Index.CompareTo(right.Index) : left.Slot.CompareTo(right.Slot);
                });
                _heapRunOrder = order;
                _heapRunIndexes = Array.ConvertAll(order, static entry => entry.Index);
                _heapRunSamplerStart = Array.FindIndex(order, static entry => entry.Slot >= BindlessHeap.ImageKinds.Length);
                if (_heapRunSamplerStart < 0) _heapRunSamplerStart = order.Length;
                _heapRunVersion = _heapEntriesVersion;
            }

            // The arrays are replaced, never changed, when the entries change: a nested revalidation leaves these intact.
            var runOrder = _heapRunOrder!;
            var runIndexes = _heapRunIndexes!;
            var samplerStart = _heapRunSamplerStart;
            var visitor = new HeapRunVisitor(this, runOrder);
            var buffer = (_heapRunBytes ??= new byte[HeapRunMaxBytes]).AsSpan();
            VisitHeapRuns(ref visitor, runIndexes, 0, samplerStart, sampler: false, buffer, scratch);
            VisitHeapRuns(ref visitor, runIndexes, samplerStart, runOrder.Length, sampler: true, buffer, scratch);
        }

        // The record's words, or none when the heap has no such record or it cannot be read.
        private uint[] ReadHeapRecord(int slot, uint index)
        {
            Span<uint> words = stackalloc uint[8];
            return words[..ReadHeapRecord(slot, index, words)].ToArray();
        }

        // Revalidation compares current words in scratch; only a changed entry needs a new retained array.
        private int ReadHeapRecord(int slot, uint index, Span<uint> words)
        {
            var sampler = slot >= BindlessHeap.ImageKinds.Length;
            var heap = sampler ? _samplerHeap : _textureHeap;
            var bytes = sampler ? BindlessHeap.SamplerRecordBytes : BindlessHeap.ImageRecordBytes;
            if (heap.Base == 0 || index >= heap.Records)
            {
                return 0;
            }

            var count = checked((int)(bytes / sizeof(uint)));
            return _guestMemory.TryRead(heap.Base + (ulong)index * bytes, MemoryMarshal.AsBytes(words[..count])) ? count : 0;
        }

        private void ResolveHeapEntry(HeapEntry entry)
        {
            var released = DetachHeapEntry(entry);
            if (entry.Slot >= BindlessHeap.ImageKinds.Length)
            {
                WriteHeapSampler(entry);
            }
            else
            {
                ImageView view = default;
                if (entry.Words.Length == 8 && ResourceMaterializer.IsFloatHeapImage(entry.Words))
                {
                    view = AcquireHeapImage(entry);
                }

                WriteHeapImage(entry.Slot, entry.Index, view);
            }

            // A deleted image is freed once no element of the heap names its view.
            released?.Invoke();
        }

        private static ShaderImageShape HeapImageShape(int slot)
        {
            var heapClass = BindlessHeap.ImageClasses[slot];
            var dimension = heapClass.Dimension;
            return new ShaderImageShape(
                Volume: dimension == ImageDimension.Dim3D,
                Arrayed: heapClass.Cube || dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray,
                Cube: heapClass.Cube,
                Storage: false,
                DynamicMip: false,
                NumericClass: TextureNumericClass.Float,
                OneDimensional: dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray,
                DepthCompare: heapClass.Compare);
        }

        private static bool IsDepthViewFormat(Format format) =>
            format is Format.D16Unorm or Format.D32Sfloat or Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;

        // Finds the record's image the way a draw finds a texture, and leaves it in the general layout the heap reads.
        private ImageView AcquireHeapImage(HeapEntry entry)
        {
            TextureRequestResolution resolution;
            try
            {
                using (ImageRequestBuilders.RejectInvalidDescriptors())
                {
                    resolution = ImageRequestBuilders.Texture(entry.Words, HeapImageShape(entry.Slot));
                }
            }
            catch (TextureDescriptorRejectedException)
            {
                return default;
            }

            var request = resolution.Request;
            if (ImageDescription.IsEmptyRange(request.Description.Data))
            {
                return default;
            }

            _ = BeginBatchedGuestCommands();
            CompleteOverlappingPublications(request.Description.Data.Address, request.Description.Data.Size, includeLabels: false);
            _imageCache.SynchronizeColorMetadata(request);
            _ = BeginBatchedGuestCommands();
            var identifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
            resolution = resolution with { Request = request };
            identifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, identifier, resolution);
            _imageCache.ApplyNativeColorClear(identifier, resolution.Request);
            var image = _imageCache.GetImage(identifier);
            var viewDescription = resolution.Request.View;
            // Depth-reference sampling needs a depth view; an integer view cannot stand in for the float class.
            if (!image.SupportsViewType(viewDescription) || ViewFormatRules.IsIntegerFormat(viewDescription.Format) ||
                (BindlessHeap.ImageClasses[entry.Slot].Compare && !IsDepthViewFormat(viewDescription.Format)))
            {
                return default;
            }

            var view = _imageCache.AcquireTextureView(identifier, resolution.Request);
            image.Transition(AccessFlags.ShaderReadBit, null, BeginBatchedGuestCommands());
            entry.Image = image;
            entry.ImageIdentifier = identifier;
            entry.Request = resolution.Request;
            entry.Resolved = true;
            if (!_heapEntriesByImage.TryGetValue(image, out var entries))
            {
                entries = [];
                _heapEntriesByImage[image] = entries;
                image.HeapChanged = _heapImageChanged;
            }

            entries.Add(entry);
            return view;
        }

        // Returns the held erase of an image deleted while the heap named it, when this was its last element.
        private Action? DetachHeapEntry(HeapEntry entry)
        {
            Action? released = null;
            if (entry.Image is { } image && _heapEntriesByImage.TryGetValue(image, out var entries))
            {
                entries.Remove(entry);
                if (entries.Count == 0)
                {
                    _heapEntriesByImage.Remove(image);
                    image.HeapChanged = null;
                    released = image.DeferredErase;
                    image.DeferredErase = null;
                }
            }

            entry.Image = null;
            entry.ImageIdentifier = default;
            entry.Resolved = false;
            return released;
        }

        private void WriteHeapImage(int slot, uint index, ImageView view)
        {
            var info = new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.General };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _heapSet,
                DstBinding = (uint)slot,
                DstArrayElement = index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &info,
            };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }

        private void WriteHeapSampler(HeapEntry entry)
        {
            var sampler = _heapNullSampler;
            if (entry.Words.Length == 4 && !(entry.Words[0] == 0xFEFEFEFE && entry.Words[1] == 0xFEFEFEFE))
            {
                Span<uint> native = stackalloc uint[4] { entry.Words[0], entry.Words[1], entry.Words[2], entry.Words[3] };
                if (entry.Slot != BindlessHeap.CompareSamplerSlot)
                {
                    native[0] &= ~(0x7u << 12);
                }

                sampler = _samplerStore.GetSampler(new SamplerDescriptorWords(native), integerView: false);
                entry.Resolved = true;
            }

            var info = new DescriptorImageInfo { Sampler = sampler };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _heapSet,
                DstBinding = (uint)entry.Slot,
                DstArrayElement = entry.Index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.Sampler,
                PImageInfo = &info,
            };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }

        // Heap images whose contents, layout or registration changed: a replaced image is found again, a CPU-written one
        // is uploaded again, and each goes back to the general layout. A target of the draw being prepared waits.
        private void RefreshChangedHeapImages()
        {
            CachedImage[] changed;
            lock (_heapChangedImages)
            {
                if (_heapChangedImages.Count == 0)
                {
                    return;
                }

                changed = [.. _heapChangedImages];
                _heapChangedImages.Clear();
            }

            List<CachedImage>? waiting = null;
            foreach (var image in changed)
            {
                if (!_heapEntriesByImage.TryGetValue(image, out var entries) || entries.Count == 0)
                {
                    continue;
                }

                var first = entries[0];
                if (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind ||
                    !_imageCache.TryGetImage(first.ImageIdentifier, out var found) || !ReferenceEquals(found, image))
                {
                    foreach (var entry in entries.ToArray())
                    {
                        ResolveHeapEntry(entry);
                    }

                    continue;
                }

                if (image.Binding.IsTarget)
                {
                    (waiting ??= []).Add(image);
                    continue;
                }

                if (image.IsCpuDirty)
                {
                    _ = _imageCache.AcquireTextureView(first.ImageIdentifier, first.Request);
                }

                image.Transition(AccessFlags.ShaderReadBit, null, BeginBatchedGuestCommands());
            }

            if (waiting is not null)
            {
                lock (_heapChangedImages)
                {
                    foreach (var image in waiting)
                    {
                        _heapChangedImages.Add(image);
                    }
                }
            }
        }
    }
}
