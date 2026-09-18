// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SharpEmu.ShaderCompiler.Resources;

// Resolves a plan against one draw: descriptor sources, indirect image tables and the
// specialization. Both outputs change only when the whole materialisation succeeds.
public static class ResourceMaterializer
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;
    private const ulong MaxIndirectImageProbes = 65536;

    // Written to standard error like every specialization refusal; the host turns it
    // into its fatal.
    public static Action<string> SpecializationFailed { get; set; } = message => Console.Error.WriteLine($"shader resource specialization failed: {message}");

    private sealed class IndirectImageTable
    {
        public uint Resource;
        public List<uint> Keys = [];
        public List<uint> Candidates = [];
        public List<DescriptorWords> Descriptors = [];
        public uint[] MaterialDescriptor = [];
        public uint[] HeapDescriptor = [];
        public uint SelectorStride;
        public uint SelectorOffset;
        public IndirectSelectorDiagnostic? SelectorDiagnostic;
    }

    private sealed class MaterializedSnapshot
    {
        public uint[][] Buffers = [];
        public uint[][] Images = [];
        public uint[][] Samplers = [];
        public uint[] FlattenedTable = [];
        public uint[] UserData = [];
        public List<IndirectImageTable> IndirectImages = [];
    }

    public static bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        Action<IndirectImageFailure>? captureIndirectImageFailure = null)
        => Materialize(plan, inputs, ref snapshot, ref specialization, out _, captureIndirectImageFailure);

    public static bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure = null)
    {
        using var totalProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Total);
        if (!MaterializeSnapshot(plan, inputs, captureIndirectImageFailure is not null, out var materialized, out failure))
        {
            return false;
        }

        // The written ranges follow the table reads so every store can check its own.
        DeviceAddressRange[] ranges;
        using (ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.DeviceAddressRanges))
            ranges = DeviceAddressRangePlanner.Evaluate(plan, inputs);
        foreach (var range in ranges)
        {
            if (!plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            var offset = (int)slot;
            materialized.FlattenedTable[offset] = (uint)range.Base;
            materialized.FlattenedTable[offset + 1] = (uint)(range.Base >> 32);
            materialized.FlattenedTable[offset + 2] = (uint)Math.Min(range.Size, uint.MaxValue);
        }

        if (!BuildSpecialization(plan, materialized, out var nextSnapshot, out var nextSpecialization, out failure, captureIndirectImageFailure))
        {
            return false;
        }

        using var outputProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.OutputAssembly);
        snapshot = new ResourceSnapshot
        {
            Buffers = nextSnapshot.Buffers,
            Images = nextSnapshot.Images,
            Samplers = nextSnapshot.Samplers,
            FlattenedResourceTable = nextSnapshot.FlattenedTable,
            UserData = nextSnapshot.UserData,
            DeviceAddressRanges = ranges,
        };
        specialization = nextSpecialization;
        failure = ResourceMaterializationFailure.None;
        return true;
    }

    // ---- snapshot ----

    private static bool MaterializeSnapshot(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        bool captureSelectorDiagnostic, out MaterializedSnapshot snapshot, out ResourceMaterializationFailure failure)
    {
        using var snapshotProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Snapshot);
        failure = ResourceMaterializationFailure.Other;
        snapshot = new MaterializedSnapshot();
        if (plan.RequiresSpecializationMemory && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        if (!RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources, inputs, plan.CleanFlatSlots,
            evaluateTable: true, out var values, out var table, out var activeSources,
            additionalTableWords: checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount)))
        {
            return false;
        }

        var cursor = 0;
        snapshot.Buffers = new uint[plan.Info.Buffers.Count][];
        for (var index = 0; index < snapshot.Buffers.Length; index++)
            snapshot.Buffers[index] = values[cursor++].Dwords;
        snapshot.FlattenedTable = table;
        snapshot.Images = new uint[plan.Info.Images.Count][];
        for (var imageIndex = 0; imageIndex < plan.Info.Images.Count; imageIndex++)
        {
            var image = plan.Info.Images[imageIndex];
            var source = plan.DescriptorSources[(int)image.Source];
            if (source.IndirectImage is { } indirect)
            {
                if (activeSources.Length != 0 && !activeSources[image.Source])
                {
                    snapshot.Images[imageIndex] = new uint[8];
                    continue;
                }
                var cleanInputs = inputs.WithReader(inputs.ReadCleanMemory);
                if (indirect.DirectCandidates is { } directCandidates)
                {
                    if (!RuntimeValueEvaluator.EvaluateSources(plan, directCandidates.Select(candidate => candidate.Source).ToArray(),
                        cleanInputs, [], evaluateTable: false, out var descriptors, out _)) return false;
                    var directTable = new IndirectImageTable { Resource = (uint)imageIndex };
                    for (var candidateIndex = 0; candidateIndex < descriptors.Count; candidateIndex++)
                    {
                        var descriptor = descriptors[candidateIndex];
                        if (NullImageDescriptor(descriptor.Dwords) || !ValidImageDescriptor(descriptor.Dwords, image.R128))
                            descriptor = DescriptorWords.Empty(8);
                        var existing = directTable.Descriptors.FindIndex(candidate => candidate.SameAs(descriptor));
                        if (existing < 0)
                        {
                            existing = directTable.Descriptors.Count;
                            directTable.Descriptors.Add(descriptor);
                        }
                        directTable.Keys.Add(directCandidates[candidateIndex].Offset);
                        directTable.Candidates.Add((uint)existing);
                    }
                    snapshot.Images[imageIndex] = directTable.Descriptors[(int)directTable.Candidates[0]].Dwords;
                    if (directTable.Descriptors.Count > 1) snapshot.IndirectImages.Add(directTable);
                    continue;
                }
                if (!RuntimeValueEvaluator.EvaluateSources(plan, [indirect.MaterialSource, indirect.HeapSource], cleanInputs, [], evaluateTable: false, out var tables, out _))
                {
                    return false;
                }

                if (!MaterializeIndirectImage(plan, indirect, tables[0], tables[1], image.R128, inputs,
                    captureSelectorDiagnostic, out var indirectTable, out failure))
                {
                    return false;
                }

                snapshot.Images[imageIndex] = indirectTable.Descriptors[(int)indirectTable.Candidates[0]].Dwords;
                if (indirectTable.Descriptors.Count > 1)
                {
                    indirectTable.Resource = (uint)imageIndex;
                    snapshot.IndirectImages.Add(indirectTable);
                }
            }
            else
            {
                var descriptor = values[cursor++];
                if (!ValidImageDescriptor(descriptor.Dwords, image.R128))
                {
                    descriptor = DescriptorWords.Empty(descriptor.DwordCount);
                }

                snapshot.Images[imageIndex] = descriptor.Dwords;
            }
        }

        snapshot.Samplers = new uint[plan.Info.Samplers.Count][];
        for (var index = 0; index < snapshot.Samplers.Length; index++)
            snapshot.Samplers[index] = values[cursor++].Dwords;
        snapshot.UserData = inputs.UserData.ToArray();
        return true;
    }

    private static bool NullImageDescriptor(ReadOnlySpan<uint> descriptor) =>
        descriptor[0] == 0 && (descriptor[1] & 0xFF) == 0;

    private static bool ValidImageDescriptor(ReadOnlySpan<uint> descriptor, bool r128)
    {
        var type = GuestImageFormat.ImageTypeOf(descriptor);
        var format = GuestImageFormat.FormatOf(descriptor);
        if (type < GuestImageFormat.ImageType1D || format == GuestImageFormat.Invalid || format > GuestImageFormat.MaxFormat)
        {
            return false;
        }

        if (r128 && type is not (GuestImageFormat.ImageType1D or GuestImageFormat.ImageType2D or GuestImageFormat.ImageType2DMsaa))
        {
            return false;
        }

        if (type is GuestImageFormat.ImageType2DMsaa or GuestImageFormat.ImageType2DMsaaArray)
        {
            var baseLevel = (descriptor[3] >> 12) & 0xF;
            var fragments = (descriptor[3] >> 16) & 0xF;
            var maxMip = (descriptor[5] >> 4) & 0xF;
            return baseLevel == 0 && fragments is >= 1 and <= 3 && (r128 || maxMip == fragments);
        }

        return true;
    }

    private static ulong ScalarBufferSize(ReadOnlySpan<uint> descriptor)
    {
        var stride = (descriptor[1] >> 16) & 0x3FFF;
        return stride == 0 ? descriptor[2] : (ulong)stride * descriptor[2];
    }

    private static bool ReadScalarBufferWord(ReadOnlySpan<uint> descriptor, uint dynamicOffset, uint immediateOffset, ResourceRuntimeInputs inputs, out uint word)
    {
        word = 0;
        var byteOffset = (ulong)dynamicOffset + immediateOffset;
        var aligned = byteOffset & ~3ul;
        var size = ScalarBufferSize(descriptor);
        if (aligned > size || size - aligned < sizeof(uint))
        {
            return true;
        }

        var baseAddress = ((descriptor[0] | ((ulong)descriptor[1] << 32)) & AddressMask) & ~3ul;
        if (aligned > AddressMask - baseAddress)
        {
            return false;
        }

        return inputs.ReadCleanMemory is not null && inputs.ReadCleanMemory(baseAddress + aligned, out word);
    }

    // Enumerates every material key that can pass the table's bounds and reads the
    // heap descriptor each selects; stale or invalid descriptors become null.
    private static bool MaterializeIndirectImage(
        ShaderResourcePlan plan,
        IndirectImageSelector indirect,
        DescriptorWords material,
        DescriptorWords heap,
        bool r128,
        ResourceRuntimeInputs inputs,
        bool captureSelectorDiagnostic,
        out IndirectImageTable result,
        out ResourceMaterializationFailure failure)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        if (material.DwordCount != 4 || heap.DwordCount != 4)
        {
            return false;
        }

        var materialStride = (material.Dwords[1] >> 16) & 0x3FFF;
        if (materialStride != indirect.SelectorStride)
        {
            return false;
        }

        var period = 1ul << 32;
        var step = (ulong)BigInteger.GreatestCommonDivisor(indirect.SelectorStride, period);
        var residue = indirect.SelectorOffset % step;
        var size = ScalarBufferSize(material.Dwords);
        var limit = Math.Min(uint.MaxValue, size + 3);
        var probeCount = residue <= limit ? (limit - residue) / step + 1 : 0;
        uint[]? provenOffsets = null;
        var diagnostic = captureSelectorDiagnostic ? new IndirectSelectorDiagnostic() : null;
        if (indirect.SelectorValues is { } selectorValues)
        {
            var evaluated = selectorValues.TryEvaluate(plan, inputs, out var selectors, diagnostic);
            if (evaluated)
                provenOffsets = selectors.Select(selector => unchecked(selector * indirect.SelectorStride + indirect.SelectorOffset)).Distinct().ToArray();
            if (diagnostic is not null)
            {
                diagnostic.SelectionMode = evaluated ? "bounded" : "full_domain_evaluation_failed";
                diagnostic.SelectorIndices = selectors;
                diagnostic.ProvenOffsets = provenOffsets ?? [];
            }
        }
        if (provenOffsets is null && probeCount > MaxIndirectImageProbes)
        {
            return false;
        }

        var keys = new List<uint> { 0 };
        var seen = new HashSet<uint> { 0 };
        var offsets = provenOffsets is not null ? provenOffsets.Select(offset => (ulong)offset) :
            Enumerable.Range(0, (int)probeCount).Select(index => residue + (ulong)index * step);
        foreach (var offset in offsets)
        {
            if (!ReadScalarBufferWord(material.Dwords, (uint)offset, 0, inputs, out var key))
            {
                return false;
            }

            if (seen.Add(key))
            {
                keys.Add(key);
                diagnostic?.KeyProbes.Add(new((uint)offset, key));
            }

        }

        var table = new IndirectImageTable
        {
            Keys = keys,
            MaterialDescriptor = material.Dwords,
            HeapDescriptor = heap.Dwords,
            SelectorStride = indirect.SelectorStride,
            SelectorOffset = indirect.SelectorOffset,
            SelectorDiagnostic = diagnostic,
        };
        foreach (var key in keys)
        {
            var candidate = new uint[8];
            var heapOffset = key << 5;
            for (uint dword = 0; dword < 8; dword++)
            {
                if (!ReadScalarBufferWord(heap.Dwords, heapOffset, dword * sizeof(uint), inputs, out candidate[dword]))
                {
                    return false;
                }
            }

            if (NullImageDescriptor(candidate) || !ValidImageDescriptor(candidate, r128))
            {
                Array.Clear(candidate);
            }

            var words = new DescriptorWords(candidate);
            var found = table.Descriptors.FindIndex(existing => existing.SameAs(words));
            if (found < 0)
            {
                if (table.Descriptors.Count >= ShaderResourceInfo.MaxImages)
                {
                    failure = ResourceMaterializationFailure.ImageCapacityExceeded;
                    return Fail("indirect image candidates exceed the dense image resource limit");
                }

                table.Descriptors.Add(words);
                table.Candidates.Add((uint)(table.Descriptors.Count - 1));
            }
            else
            {
                table.Candidates.Add((uint)found);
            }
        }

        result = table;
        return true;
    }

    // ---- specialization ----

    private static ImageDimension DescriptorDimension(ReadOnlySpan<uint> descriptor, ImageDimension requested)
    {
        var isArray = requested is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
        return GuestImageFormat.ImageTypeOf(descriptor) switch
        {
            GuestImageFormat.ImageType1D => ImageDimension.Dim1D,
            GuestImageFormat.ImageType1DArray => isArray ? ImageDimension.Dim1DArray : ImageDimension.Dim1D,
            GuestImageFormat.ImageType3D => ImageDimension.Dim3D,
            GuestImageFormat.ImageTypeCube => ImageDimension.Dim2DArray,
            GuestImageFormat.ImageType2DArray => isArray ? ImageDimension.Dim2DArray : ImageDimension.Dim2D,
            GuestImageFormat.ImageType2DMsaaArray => isArray ? ImageDimension.Dim2DMsaaArray : ImageDimension.Dim2DMsaa,
            GuestImageFormat.ImageType2D => ImageDimension.Dim2D,
            GuestImageFormat.ImageType2DMsaa => ImageDimension.Dim2DMsaa,
            _ => ImageDimension.Unknown,
        };
    }

    private static uint ImageConversionFormat(uint format) =>
        GuestImageFormat.Remap(format) != format ? format : GuestImageFormat.Invalid;

    private static bool RequiresPointSampler(ImageNumericClass numericClass, uint conversionFormat) =>
        numericClass == ImageNumericClass.Sint || conversionFormat != GuestImageFormat.Invalid;

    private static uint StorageMipCount(ImageResource image, ReadOnlySpan<uint> descriptor)
    {
        if (image.MipMode != ImageMipMode.DynamicStorage || NullImageDescriptor(descriptor))
        {
            return 1;
        }

        var baseLevel = (descriptor[3] >> 12) & 0xF;
        var last = (descriptor[3] >> 16) & 0xF;
        return baseLevel <= last ? last - baseLevel + 1 : 0;
    }

    private static bool Fail(string message)
    {
        SpecializationFailed(message);
        return false;
    }

    private static bool BuildSpecialization(
        ShaderResourcePlan plan,
        MaterializedSnapshot snapshot,
        out MaterializedSnapshot specializedSnapshot,
        out ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure)
    {
        using var specializationProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Specialization);
        failure = ResourceMaterializationFailure.Other;
        specializedSnapshot = snapshot;
        specialization = new ResourceSpecialization();
        var info = plan.Info;
        var imageCount = info.Images.Count;
        var mappingWordCount = 0;
        foreach (var table in snapshot.IndirectImages)
        {
            if (table.Resource >= info.Images.Count || table.Descriptors.Count < 2)
            {
                return Fail("indirect image table has an invalid root or candidate count");
            }

            if (imageCount + table.Descriptors.Count - 1 > ShaderResourceInfo.MaxImages)
            {
                failure = ResourceMaterializationFailure.ImageCapacityExceeded;
                return Fail("indirect image candidates exceed the dense image resource limit");
            }

            imageCount += table.Descriptors.Count - 1;
            mappingWordCount = checked(mappingWordCount + 1 + table.Keys.Count * 2);
        }

        // Each draw owns these arrays. Only indirect candidates require a larger table.
        Array.Resize(ref snapshot.Images, imageCount);
        var mappingCursor = snapshot.FlattenedTable.Length;
        Array.Resize(ref snapshot.FlattenedTable, checked(mappingCursor + mappingWordCount));
        var imageCursor = info.Images.Count;
        var images = new List<ImageSpecialization>(imageCount);
        foreach (var image in info.Images)
        {
            images.Add(new ImageSpecialization(
                image.NumericClass, image.Dimension, image.MipCount, image.ConversionFormat, image.ShaderSwizzle,
                image.IndirectRoot, image.IndirectMappingOffset, image.IndirectSearchIterations, image.Cube));
        }

        foreach (var table in snapshot.IndirectImages)
        {
            var rootImage = images[(int)table.Resource];
            for (var candidate = 1; candidate < table.Descriptors.Count; candidate++)
            {
                images.Add(rootImage with { IndirectRoot = table.Resource });
                snapshot.Images[imageCursor++] = table.Descriptors[candidate].Dwords;
            }

            var mappingOffset = (uint)mappingCursor;
            images[(int)table.Resource] = rootImage with
            {
                IndirectRoot = table.Resource,
                IndirectMappingOffset = mappingOffset,
                IndirectSearchIterations = (uint)BitOperations.Log2((uint)table.Keys.Count) + 1,
            };
            mappingCursor += 1 + table.Keys.Count * 2;
            var order = Enumerable.Range(0, table.Keys.Count).OrderBy(index => table.Keys[index]).ToArray();
            snapshot.FlattenedTable[(int)mappingOffset] = (uint)table.Keys.Count;
            for (var entry = 0; entry < order.Length; entry++)
            {
                var source = order[entry];
                var offset = (int)mappingOffset + 1 + entry * 2;
                snapshot.FlattenedTable[offset] = table.Keys[source];
                snapshot.FlattenedTable[offset + 1] = table.Candidates[source];
            }

            snapshot.Images[(int)table.Resource] = table.Descriptors[0].Dwords;
        }

        var buffers = new List<BufferSpecialization>(info.Buffers.Count);
        for (var index = 0; index < info.Buffers.Count; index++)
        {
            var descriptor = snapshot.Buffers[index];
            if (descriptor.Length != 4)
            {
                return Fail($"buffer descriptor {index} has invalid width");
            }

            var words = descriptor;
            if ((words[3] >> 30) != 0)
            {
                Array.Clear(words);
            }

            var stride = (words[1] >> 16) & 0x3FFF;
            var swizzleEnabled = (words[1] >> 31) != 0;
            var indexStride = (words[3] >> 21) & 0x3;
            var addThreadId = (words[3] >> 23) & 0x1;
            var packedStride = stride | ((swizzleEnabled ? 1u : 0u) << 14) | (indexStride << 16) | (addThreadId << 20);
            var swizzle = stride != 0 && ((packedStride >> 14) & 1) != 0;
            if (stride == 0)
            {
                packedStride &= ~((1u << 14) | (3u << 16));
            }
            else if (!swizzle)
            {
                packedStride &= ~(3u << 16);
            }

            var formatted = info.Buffers[index].Formatted;
            buffers.Add(new BufferSpecialization(
                packedStride,
                formatted ? (words[3] >> 12) & 0x7F : DescriptorConstants.InvalidFormat,
                formatted ? words[3] & 0xFFF : DescriptorConstants.IdentityDestinationSelect));
        }

        for (var index = 0; index < images.Count; index++)
        {
            var descriptor = snapshot.Images[index];
            var image = images[index];
            var baseIndex = index < info.Images.Count ? (uint)index : image.IndirectRoot;
            if (baseIndex >= info.Images.Count)
            {
                return Fail($"image resource {index} has an invalid root");
            }

            var baseImage = info.Images[(int)baseIndex];
            if (baseImage.ResourceClass == ImageResourceClass.None || (baseImage.Atomic && baseImage.ResourceClass != ImageResourceClass.Storage))
            {
                return Fail($"image resource {index} has an invalid class");
            }

            var mipCount = StorageMipCount(baseImage, descriptor);
            if (mipCount == 0)
            {
                return Fail($"storage image descriptor {index} has an invalid mip range");
            }

            image = image with { MipCount = mipCount };
            if (NullImageDescriptor(descriptor))
            {
                images[index] = image with
                {
                    NumericClass = baseImage.Atomic ? ImageNumericClass.Uint : ImageNumericClass.Float,
                    Dimension = ImageDimension.Dim2D,
                    Cube = false,
                };
                continue;
            }

            var dimension = DescriptorDimension(descriptor, baseImage.Dimension);
            if (dimension == ImageDimension.Unknown)
            {
                return Fail(
                    $"image descriptor {index} has unsupported type {GuestImageFormat.ImageTypeOf(descriptor)}: " +
                    string.Join(",", descriptor.Select(word => $"{word:x8}")));
            }

            var format = GuestImageFormat.FormatOf(descriptor);
            if (baseImage.Atomic && format != GuestImageFormat.Format32Uint)
            {
                return Fail($"atomic image descriptor {index} uses unsupported format {format}");
            }

            var storage = baseImage.ResourceClass == ImageResourceClass.Storage;
            var conversionFormat = ImageConversionFormat(format);
            var shaderSwizzle = storage || conversionFormat != GuestImageFormat.Invalid ? descriptor[3] & 0xFFF : image.ShaderSwizzle;
            var rawSintStorage = storage && format == GuestImageFormat.Format32Sint && baseImage.Written && !baseImage.Read && !baseImage.Atomic;
            var numericClass = GuestImageFormat.SampledNumericClass(format);
            if (storage)
            {
                if ((!rawSintStorage && numericClass == ImageNumericClass.Sint) || numericClass == ImageNumericClass.Unsupported)
                {
                    return Fail($"storage image descriptor {index} uses unsupported format {format}");
                }

                if (rawSintStorage)
                {
                    numericClass = ImageNumericClass.Uint;
                }
            }
            else if (numericClass == ImageNumericClass.Unsupported || (baseImage.DepthCompare && numericClass != ImageNumericClass.Float))
            {
                return Fail($"sampled image descriptor {index} uses unsupported format {format}");
            }

            images[index] = image with
            {
                Dimension = dimension,
                Cube = GuestImageFormat.ImageTypeOf(descriptor) == GuestImageFormat.ImageTypeCube,
                ConversionFormat = conversionFormat,
                ShaderSwizzle = shaderSwizzle,
                NumericClass = numericClass,
            };
        }

        for (var rootIndex = 0; rootIndex < images.Count; rootIndex++)
        {
            var root = images[rootIndex];
            if (root.IndirectRoot != rootIndex)
            {
                continue;
            }

            var keyCount = root.IndirectMappingOffset < snapshot.FlattenedTable.Length ? snapshot.FlattenedTable[(int)root.IndirectMappingOffset] : 0;
            if (root.IndirectSearchIterations == 0 || keyCount < 2 ||
                (ulong)root.IndirectMappingOffset + 1 + (ulong)keyCount * 2 > (ulong)snapshot.FlattenedTable.Length)
            {
                return Fail("indirect image specialization has an invalid key mapping");
            }

            var exemplar = DescriptorConstants.NoIndex;
            var resourceCount = 0;
            for (var resource = 0; resource < images.Count; resource++)
            {
                if (images[resource].IndirectRoot != rootIndex)
                {
                    continue;
                }

                resourceCount++;
                if (exemplar == DescriptorConstants.NoIndex && !NullImageDescriptor(snapshot.Images[resource]))
                {
                    exemplar = (uint)resource;
                }
            }

            if (resourceCount < 2 || exemplar == DescriptorConstants.NoIndex)
            {
                return Fail("indirect image specialization has no typed candidate");
            }

            var imageClass = images[(int)exemplar];
            for (var candidate = 0; candidate < images.Count; candidate++)
            {
                var image = images[candidate];
                if (image.IndirectRoot != rootIndex)
                {
                    continue;
                }

                if (NullImageDescriptor(snapshot.Images[candidate]))
                {
                    image = image with
                    {
                        NumericClass = imageClass.NumericClass,
                        Dimension = imageClass.Dimension,
                        MipCount = imageClass.MipCount,
                        ConversionFormat = imageClass.ConversionFormat,
                        ShaderSwizzle = imageClass.ShaderSwizzle,
                        Cube = imageClass.Cube,
                    };
                    images[candidate] = image;
                }

                // Sampled 2D cases use their own binding and coordinate width.
                var separateDimensions = info.Images[rootIndex].ResourceClass == ImageResourceClass.Sampled &&
                    !image.Cube && !imageClass.Cube &&
                    image.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray &&
                    imageClass.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray;
                if (image.NumericClass != imageClass.NumericClass || (!separateDimensions && image.Dimension != imageClass.Dimension) ||
                    image.MipCount != imageClass.MipCount || image.ConversionFormat != imageClass.ConversionFormat ||
                    image.ShaderSwizzle != imageClass.ShaderSwizzle || image.Cube != imageClass.Cube)
                {
                    failure = ResourceMaterializationFailure.IncompatibleImageCandidates;
                    if (captureIndirectImageFailure is not null)
                    {
                        var table = snapshot.IndirectImages.Single(table => table.Resource == rootIndex);
                        captureIndirectImageFailure(new IndirectImageFailure(
                            info.Images[rootIndex].FirstUsePc, (uint)rootIndex, exemplar, (uint)candidate,
                            table.SelectorStride, table.SelectorOffset,
                            [.. table.MaterialDescriptor], [.. table.HeapDescriptor],
                            [.. table.Keys], [.. table.Candidates],
                            table.Descriptors.Select(words => words.Dwords.ToArray()).ToArray(),
                            snapshot.Images.Select(words => words.ToArray()).ToArray(),
                            [.. images], [.. snapshot.UserData])
                        {
                            SelectorDiagnostic = table.SelectorDiagnostic,
                            ScalarReads = plan.TableReads.Select(read =>
                            {
                                var access = plan.Memory[read.Value.MemoryIndex];
                                return new MaterializedScalarRead(access.Pc, access.ComponentIndex, read.FlatOffset,
                                    snapshot.FlattenedTable[(int)read.FlatOffset]);
                            }).ToArray(),
                        });
                    }
                    return Fail($"indirect image table at pc 0x{info.Images[rootIndex].FirstUsePc:x8} has incompatible candidates: exemplar={exemplar} candidate={candidate}");
                }
            }
        }

        if (!BuildSamplerPlan(info, images, out var samplerPlan))
        {
            return Fail("specialized sampler layout exceeds its resource limit");
        }

        Array.Resize(ref snapshot.Samplers, checked((int)samplerPlan.SamplerCount));
        for (var index = 0; index < info.Samplers.Count; index++)
        {
            var target = samplerPlan.PointSampler[index];
            if (target != DescriptorConstants.NoIndex && target >= info.Samplers.Count)
            {
                snapshot.Samplers[target] = snapshot.Samplers[index];
            }
        }

        specialization = new ResourceSpecialization { Buffers = buffers, Images = images };
        specializedSnapshot = snapshot;
        return true;
    }

    private sealed class SamplerPlan
    {
        public uint[] PointSampler = new uint[ShaderResourceInfo.MaxSamplers];
        public uint SamplerCount;
    }

    // A sampler that some pair uses with a point-only image needs a point-filtering
    // copy; when every pair does, the sampler itself switches.
    private static bool BuildSamplerPlan(ShaderResourceInfo info, IReadOnlyList<ImageSpecialization> images, out SamplerPlan plan)
    {
        plan = new SamplerPlan();
        if (info.Samplers.Count > plan.PointSampler.Length)
        {
            return false;
        }

        Array.Fill(plan.PointSampler, DescriptorConstants.NoIndex);
        plan.SamplerCount = (uint)info.Samplers.Count;
        var usage = new byte[ShaderResourceInfo.MaxSamplers];
        foreach (var pair in info.SampledPairs)
        {
            if (pair.Image >= images.Count || pair.Sampler >= info.Samplers.Count)
            {
                return false;
            }

            var image = images[(int)pair.Image];
            usage[pair.Sampler] |= RequiresPointSampler(image.NumericClass, image.ConversionFormat) ? (byte)2 : (byte)1;
        }

        for (var index = 0; index < info.Samplers.Count; index++)
        {
            if ((usage[index] & 2) == 0)
            {
                continue;
            }

            if ((usage[index] & 1) == 0)
            {
                plan.PointSampler[index] = (uint)index;
            }
            else
            {
                if (plan.SamplerCount >= ShaderResourceInfo.MaxSamplers)
                {
                    return false;
                }

                plan.PointSampler[index] = plan.SamplerCount++;
            }
        }

        return true;
    }

    // Applies a specialization to the plan's tables: buffer strides and formats, image
    // classes and indirect candidates, point samplers and the sampler each access uses.
    public static SpecializedResourceInfo ApplyTo(ShaderResourcePlan plan, ResourceSpecialization specialization)
    {
        var source = plan.Info;
        if (source.Buffers.Count != specialization.Buffers.Count || source.Images.Count > specialization.Images.Count)
        {
            throw new ResourcePlanException(
                $"shader resource specialization does not match the plan: hash=0x{plan.Hash:X16} stage={plan.Stage} " +
                $"buffers={source.Buffers.Count}/{specialization.Buffers.Count} images={source.Images.Count}/{specialization.Images.Count}");
        }

        var info = source.Clone();
        for (var index = 0; index < info.Buffers.Count; index++)
        {
            info.Buffers[index].PackedStride = specialization.Buffers[index].PackedStride;
            info.Buffers[index].DescriptorFormat = specialization.Buffers[index].DescriptorFormat;
            info.Buffers[index].DescriptorSwizzle = specialization.Buffers[index].DescriptorSwizzle;
        }

        for (var index = 0; index < specialization.Images.Count; index++)
        {
            var specialized = specialization.Images[index];
            if (index >= info.Images.Count)
            {
                if (specialized.IndirectRoot >= source.Images.Count)
                {
                    throw new ResourcePlanException($"shader resource specialization names an invalid indirect root: hash=0x{plan.Hash:X16} image={index}");
                }

                info.Images.Add(source.Images[(int)specialized.IndirectRoot].Clone());
            }

            var image = info.Images[index];
            image.NumericClass = specialized.NumericClass;
            image.Dimension = specialized.Dimension;
            image.MipCount = specialized.MipCount;
            image.ConversionFormat = specialized.ConversionFormat;
            image.ShaderSwizzle = specialized.ShaderSwizzle;
            image.IndirectRoot = specialized.IndirectRoot;
            image.IndirectMappingOffset = specialized.IndirectMappingOffset;
            image.IndirectSearchIterations = specialized.IndirectSearchIterations;
            image.Cube = specialized.Cube;
            image.IndirectResources = [];
        }

        for (var index = 0; index < info.Images.Count; index++)
        {
            var root = info.Images[index].IndirectRoot;
            if (root != DescriptorConstants.NoIndex)
            {
                info.Images[(int)root].IndirectResources.Add((uint)index);
            }
        }

        if (!BuildSamplerPlan(source, specialization.Images, out var samplerPlan))
        {
            throw new ResourcePlanException($"shader resource specialization exceeds the sampler limit: hash=0x{plan.Hash:X16}");
        }

        for (var index = 0; index < source.Samplers.Count; index++)
        {
            var target = samplerPlan.PointSampler[index];
            if (target == DescriptorConstants.NoIndex)
            {
                continue;
            }

            if (target == index)
            {
                info.Samplers[index].ForcePointFiltering = true;
            }
            else
            {
                var sampler = source.Samplers[index].Clone();
                sampler.ForcePointFiltering = true;
                info.Samplers.Add(sampler);
            }
        }

        foreach (var pair in info.SampledPairs)
        {
            var image = info.Images[(int)pair.Image];
            if (RequiresPointSampler(image.NumericClass, image.ConversionFormat))
            {
                pair.Sampler = samplerPlan.PointSampler[pair.Sampler];
            }

            info.Samplers[(int)pair.Sampler].DepthCompare |= image.DepthCompare;
        }

        var samplerByMemory = new Dictionary<int, uint>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind != MemoryResourceKind.Image || memory.Resource >= info.Images.Count)
            {
                continue;
            }

            var image = info.Images[(int)memory.Resource];
            if (memory.NeedsSampler && RequiresPointSampler(image.NumericClass, image.ConversionFormat) && memory.Sampler < source.Samplers.Count)
            {
                samplerByMemory[index] = samplerPlan.PointSampler[memory.Sampler];
            }
        }

        return new SpecializedResourceInfo { Info = info, SamplerByMemoryIndex = samplerByMemory };
    }
}
