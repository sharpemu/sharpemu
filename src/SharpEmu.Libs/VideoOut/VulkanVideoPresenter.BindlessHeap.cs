// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Descriptor set 1: the images and samplers of call records (see BindlessHeap and VulkanVideoPresenter.CallRecords). Every
// element starts as a null descriptor; the host writes a descriptor's view or sampler when a dispatch's records first name
// it, and a record image's contents and layout are made current before each command that reads set 1.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const DescriptorBindingFlags HeapElementFlags =
            DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.PartiallyBoundBit |
            DescriptorBindingFlags.UpdateUnusedWhilePendingBit;

        // The images set 1 holds are marked in use again after this many submission ticks, so the image cache keeps them.
        private const ulong HeapRevalidationTicks = 16;

        // Programs linked with run-time call records read their records through set 1, on every device that supports it.
        bool Gpu.Pipelines.IShaderPipelineHost.RuntimeCallRecordsSupported => _bindlessHeapSupported;

        // One element of set 1: the descriptor words it was resolved from and what it holds.
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
        private readonly Dictionary<CachedImage, List<HeapEntry>> _heapEntriesByImage = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<CachedImage> _heapChangedImages = new(ReferenceEqualityComparer.Instance);
        private ulong _heapRevalidatedTick;
        private object? _heapPreparedFor;
        private Sampler _heapNullSampler;
        private Action<CachedImage>? _heapImageChanged;

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
                indexing.MaxPerStageUpdateAfterBindResources < images + samplers)
            {
                // Linked programs then link a copy of each callee per record.
                _bindlessHeapSupported = false;
                return;
            }

            var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit |
                (_supportsMeshShader ? ShaderStageFlags.MeshBitExt : 0);
            var count = BindlessHeap.ArrayCount;
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
                    "A program reads call records, but the device has no descriptor set 1 for them (it needs descriptor indexing).");

        // The set and its null elements, made at the first command that reads set 1.
        private void EnsureBindlessHeap()
        {
            if (_heapSet.Handle != 0)
            {
                return;
            }

            var layout = RequireHeapSetLayout();
            var sizes = stackalloc DescriptorPoolSize[2];
            sizes[0] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = (uint)BindlessHeap.ImageKinds.Length * BindlessHeap.Capacity };
            sizes[1] = new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = 2 * BindlessHeap.Capacity };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = 2,
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
            var writes = new WriteDescriptorSet[BindlessHeap.ArrayCount];
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

                _vk.UpdateDescriptorSets(_device, (uint)writes.Length, writePointer, 0, null);
            }
        }

        private void DestroyBindlessHeap()
        {
            foreach (var image in _heapEntriesByImage.Keys)
            {
                image.HeapChanged = null;
            }

            _heapEntriesByImage.Clear();
            _callRecordEntries.Clear();
            Array.Clear(_callRecordNext);
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

        // Before the images of a draw or dispatch that reads set 1 are found: set 1 exists, its images stay in use, and those
        // whose contents or layout changed are made current.
        internal void PrepareBindlessHeap(object preparation)
        {
            if (ReferenceEquals(_heapPreparedFor, preparation))
            {
                return;
            }

            _heapPreparedFor = preparation;
            EnsureBindlessHeap();
            if (_scheduler.CurrentTick - _heapRevalidatedTick >= HeapRevalidationTicks)
            {
                KeepHeapImagesInUse();
            }

            RefreshChangedHeapImages();
        }

        public void BindBindlessHeap(CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout)
        {
            var set = _heapSet;
            if (set.Handle == 0)
            {
                throw SubmissionScheduler.Fatal("A program reads call records before descriptor set 1 exists.");
            }

            _vk.CmdBindDescriptorSets(command, bindPoint, layout, BindlessHeap.DescriptorSet, 1, &set, 0, null);
        }

        // A call record entry is named by its words, which never change; its image stays in use.
        private void KeepHeapImagesInUse()
        {
            _heapRevalidatedTick = _scheduler.CurrentTick;
            foreach (var entry in _callRecordEntries.Values)
            {
                if (entry.Image is { } image && _imageCache.TryGetImage(entry.ImageIdentifier, out var found) && ReferenceEquals(found, image))
                {
                    _ = _imageCache.GetImage(entry.ImageIdentifier);
                }
            }
        }

        private void ResolveHeapEntry(HeapEntry entry)
        {
            DetachHeapEntry(entry);
            if (entry.Slot >= BindlessHeap.ImageKinds.Length)
            {
                WriteHeapSampler(entry);
                return;
            }

            ImageView view = default;
            if (entry.Words.Length == 8 && ResourceMaterializer.IsFloatHeapImage(entry.Words))
            {
                view = AcquireHeapImage(entry);
            }

            WriteHeapImage(entry.Slot, entry.Index, view);
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

        private void DetachHeapEntry(HeapEntry entry)
        {
            if (entry.Image is { } image && _heapEntriesByImage.TryGetValue(image, out var entries))
            {
                entries.Remove(entry);
                if (entries.Count == 0)
                {
                    _heapEntriesByImage.Remove(image);
                    image.HeapChanged = null;
                }
            }

            entry.Image = null;
            entry.ImageIdentifier = default;
            entry.Resolved = false;
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
