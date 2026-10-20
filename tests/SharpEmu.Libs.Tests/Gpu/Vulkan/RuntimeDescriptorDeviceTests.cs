// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A shader that loads its T# and S# from a buffer by a runtime index, in a loop, and reads images through
// the runtime descriptor heap: the whole contract between the compiler, the translator, the heap and the
// registry, run on a device. Each round plays the part of a tick: dispatch, read the misses, register them,
// rebuild the table, dispatch again.
[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeDescriptorDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Format32Uint = 20;
    private const uint Format32Sint = 21;
    private const uint Format32Float = 22;
    private const uint ImageType2D = 9;
    private const uint TableRegister = 4;
    private const uint ResultRegister = 8;
    private const ulong TableAddress = 0x1000;
    private const ulong ResultAddress = 0x2000;
    private const uint EntryBytes = 48;
    private const uint ResultBytes = 64;
    private const ulong ImageBase = 0x40_0000;

    // Every iteration reads the T# at table offset s40 (and the S# 32 bytes after it) and stores the first
    // component of the texel it reads to result offset s41. The counters sit outside the T# and S# registers.
    private static Gen5ShaderProgram LoopProgram(string opcode, uint entryBytes, uint entries, bool withSampler)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Func<uint, Gen5ShaderInstruction> make)
        {
            var instruction = make(pc);
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        Add(at => MoveScalar(at, 40, 0));
        Add(at => MoveScalar(at, 41, 0));
        Add(at => MoveVector(at, 0, withSampler ? 0x3F00_0000u : 0u));
        Add(at => MoveVector(at, 1, withSampler ? 0x3F00_0000u : 0u));
        var loopHead = pc;
        Add(at => ScalarBufferLoad(at, TableRegister, destination: 16, count: 8, dynamicOffsetRegister: 40));
        if (withSampler)
        {
            Add(at => ScalarBufferLoad(at, TableRegister, destination: 24, count: 4, immediateOffset: 32, dynamicOffsetRegister: 40));
        }

        Add(at => Image(at, opcode, 16, withSampler ? 24u : 0u, dimension: 1, dmask: 1));
        Add(at => Vop1(at, "VMovB32", 6, Gen5Operand.Scalar(41)));
        Add(at => BufferAccess(at, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 6));
        Add(at => Sop2(at, "SAddU32", 40, Gen5Operand.Scalar(40), Operand(entryBytes)));
        Add(at => Sop2(at, "SAddU32", 41, Gen5Operand.Scalar(41), Operand(4)));
        Add(at => Sopc(at, "SCmpLtU32", Gen5Operand.Scalar(40), Operand(entryBytes * entries)));
        Add(at => Branch(at, "SCbranchScc1", (short)((int)loopHead - (int)(at + 4) >> 2)));
        Add(EndProgram);
        return Program([.. instructions]);
    }

    private static uint[] UserData(uint tableBytes)
    {
        var registers = new uint[256];
        registers[TableRegister] = (uint)TableAddress;
        registers[TableRegister + 2] = tableBytes;
        registers[ResultRegister] = (uint)ResultAddress;
        registers[ResultRegister + 2] = ResultBytes;
        return registers;
    }

    private static uint[] ImageWords(ulong address, uint format) =>
        [(uint)(address >> 8), format << 20, 0, 0xFAC | (ImageType2D << 28), 0, 0, 0, 0];

    private static ImageDescription Describe(ulong address, Format format, GuestPixelFormat guestFormat)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayBackedSpace.Base + address, 64);
        description.PixelFormat = format;
        description.GuestFormat = guestFormat;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = 1;
        description.BytesPerBlock = 4;
        return description;
    }

    // The words of the table entry: the T#, then the S# (all zero: nearest, clamp).
    private static void WriteEntry(Span<byte> table, int index, uint entryBytes, uint[] image)
    {
        var entry = table[(index * (int)entryBytes)..];
        for (var word = 0; word < image.Length; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry[(word * 4)..], image[word]);
        }
    }

    private sealed class Scenario : IDisposable
    {
        public Scenario(HeadlessVulkan vulkan, Gen5ShaderProgram program, uint tableBytes)
        {
            Plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64, runtimeDescriptors: RuntimeDescriptorMode.OnDemand);
            var registers = UserData(tableBytes);
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(Plan, Inputs(registers), ref snapshot, ref specialization));
            Snapshot = snapshot;
            Resources = ResourceMaterializer.ApplyTo(Plan, specialization);
            Assert.True(Resources.Info.UsesRuntimeDescriptors);
            var layout = BindingLayout.Allocate(
                Resources.Info,
                BindingLayout.CollectUserDataRegisters(program, 0, 64),
                false,
                ShaderCompileRequest.RequiresFlattenedTable(Plan, Resources),
                false);
            Request = new ShaderCompileRequest(Plan, Resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request, out var shader, out var error), error);
            Harness = new ImageTestHarness(vulkan);
            Heap = Harness.Run(() => new RuntimeDescriptorHeap(Harness.Device, Harness.Scheduler, vulkan.RuntimeDescriptorLimits));
            Runner = new LayoutComputeRunner(Harness, Request, shader.Spirv, Heap.Layout);
            Registers = registers;
            Result = Runner.CreateBuffer(ResultBytes);
        }

        public ShaderResourcePlan Plan { get; }
        public ResourceSnapshot Snapshot { get; }
        public SpecializedResourceInfo Resources { get; }
        public ShaderCompileRequest Request { get; }
        public ImageTestHarness Harness { get; }
        public RuntimeDescriptorHeap Heap { get; }
        public LayoutComputeRunner Runner { get; }
        public GpuBuffer Result { get; }
        public uint[] Registers { get; }

        // The host's per-shader state, and the images it can make for the descriptors the shader reached.
        public RuntimeDescriptorRegistry<object> Registry { get; } = new();
        public Dictionary<uint, (CachedImage Image, ImageViewDescription View)> ImagesByAddressWord { get; } = [];
        public List<CachedImage> Images { get; } = [];
        public int RoundsRun { get; private set; }
        public int LastImageMisses { get; private set; }
        public int LastSamplerMisses { get; private set; }

        public void AddImage(uint addressWord, Format format, GuestPixelFormat guestFormat, byte[] texel)
        {
            var image = Harness.CreateImage(Describe((ulong)addressWord << 8, format, guestFormat));
            Harness.UploadImage(image, texel, ImageTestHarness.WholeImageCopies(image.Description, 0));
            Images.Add(image);
            ImagesByAddressWord[addressWord] = (image, ImageViewDescription.Default with { Format = format, Usage = ImageUsageFlags.SampledBit });
        }

        // One tick: dispatch with the current table, read what the shader missed, register it, rebuild the table.
        public void Round(Sampler hostSampler, GpuBuffer table, out GpuBuffer nextTable)
        {
            var misses = Runner.CreateBuffer(RuntimeDescriptorTable.MissBufferDwords * sizeof(uint));
            var boundBuffers = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.RuntimeDescriptorTable] = [table],
                [DescriptorBindingKind.RuntimeDescriptorMisses] = [misses],
            };
            var tableBuffer = Snapshot.Buffers.Select(descriptor => descriptor[0] == (uint)TableAddress ? TableBytes! : Result).ToArray();
            boundBuffers[DescriptorBindingKind.Buffers] = tableBuffer;
            Harness.Run(() =>
            {
                var command = new CommandBuffer(Harness.Scheduler.Current.Handle);
                foreach (var image in Images)
                {
                    image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
                }

                Runner.Dispatch(Registers, boundBuffers, 1, flattenedTable: Snapshot.FlattenedResourceTable, heapSet: Heap.Set);
            });
            RoundsRun++;

            var bytes = Runner.ReadBack(misses, 0, misses.Size);
            var words = MemoryMarshal.Cast<byte, uint>(bytes);
            LastImageMisses = (int)words[(int)RuntimeDescriptorTable.ImageMissCountDword];
            LastSamplerMisses = (int)words[(int)RuntimeDescriptorTable.SamplerMissCountDword];
            Registry.ReadMisses(words);
            Registry.RegisterMisses(
                (ulong)RoundsRun,
                entry => ImagesByAddressWord.ContainsKey(entry.Key[1]),
                _ => Heap.GetOrCreateSamplerSlot(hostSampler));
            foreach (var entry in Registry.Images.Values)
            {
                var (image, view) = ImagesByAddressWord[entry.Key[1]];
                entry.Slot = Heap.GetOrCreateImageSlot(image.GetOrCreateView(view), ImageLayout.ShaderReadOnlyOptimal);
            }

            var rebuilt = Registry.BuildTable();
            nextTable = Runner.CreateBuffer(MemoryMarshal.AsBytes(rebuilt.AsSpan()));
        }

        public GpuBuffer? TableBytes { get; set; }

        public uint ResultWord(int index) => BinaryPrimitives.ReadUInt32LittleEndian(Runner.ReadBack(Result, 0, ResultBytes).AsSpan(index * 4));

        public void Dispose()
        {
            Runner.Dispose();
            Heap.Dispose();
            Harness.Dispose();
        }
    }

    private static byte[] Floats(params float[] values) => MemoryMarshal.AsBytes<float>(values).ToArray();

    [Fact]
    public void SampleLoop_ReadsEachDescriptorOnceTheHostHasRegisteredIt()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        const int Count = 3;
        using var scenario = new Scenario(vulkan, LoopProgram("ImageSampleLz", EntryBytes, (uint)Count, withSampler: true), EntryBytes * (uint)Count);
        float[] expected = [0.25f, 0.5f, 0.75f];
        var tableData = new byte[EntryBytes * (uint)Count];
        for (var index = 0; index < Count; index++)
        {
            var word = (uint)(ImageBase >> 8) + (uint)index * 0x10;
            WriteEntry(tableData, index, EntryBytes, ImageWords((ulong)word << 8, Format32Float));
            scenario.AddImage(word, Format.R32Sfloat, GuestPixelFormat.Bits32Float, Floats(expected[index]));
        }

        scenario.TableBytes = scenario.Runner.CreateBuffer(tableData);
        var sampler = scenario.Runner.CreateSampler(Filter.Nearest);
        var table = scenario.Runner.CreateBuffer(MemoryMarshal.AsBytes(RuntimeDescriptorTable.Build([], []).AsSpan()));

        // Tick 0 has no table: every lookup misses, reads the null image, and records its descriptor.
        scenario.Round(sampler, table, out table);
        Assert.Equal(0u, scenario.ResultWord(0));
        Assert.Equal(0u, scenario.ResultWord(1));
        Assert.Equal(0u, scenario.ResultWord(2));
        Assert.InRange(scenario.LastImageMisses, 1, Count);
        Assert.Equal(1, scenario.LastSamplerMisses);

        // A key that shares a bit with another waits one tick for it, so a few ticks register everything.
        for (var round = 0; round < 6 && scenario.Registry.Images.Count < Count; round++)
        {
            scenario.Round(sampler, table, out table);
        }

        Assert.Equal(Count, scenario.Registry.Images.Count);
        scenario.Round(sampler, table, out table);
        for (var index = 0; index < Count; index++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[index]), scenario.ResultWord(index));
        }

        Assert.Equal(0, scenario.LastImageMisses);
        Assert.Equal(0, scenario.LastSamplerMisses);
        scenario.Harness.AssertNoValidationMessages();
        output.WriteLine($"Verified runtime image sampling after {scenario.RoundsRun} ticks on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    // A key whose home entry another key took sits further along its probe sequence; the lookup walks
    // there, past the other key, which must not match although it holds a valid slot.
    [Fact]
    public void SampleLoop_FindsKeysPlacedPastACollidingEntry()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        const int Count = 3;
        using var scenario = new Scenario(vulkan, LoopProgram("ImageSampleLz", EntryBytes, (uint)Count, withSampler: true), EntryBytes * (uint)Count);
        float[] expected = [0.25f, 0.5f, 0.75f];
        var tableData = new byte[EntryBytes * (uint)Count];
        for (var index = 0; index < Count; index++)
        {
            var word = (uint)(ImageBase >> 8) + (uint)index * 0x10;
            WriteEntry(tableData, index, EntryBytes, ImageWords((ulong)word << 8, Format32Float));
            scenario.AddImage(word, Format.R32Sfloat, GuestPixelFormat.Bits32Float, Floats(expected[index]));
        }

        scenario.TableBytes = scenario.Runner.CreateBuffer(tableData);
        var sampler = scenario.Runner.CreateSampler(Filter.Nearest);
        var table = scenario.Runner.CreateBuffer(MemoryMarshal.AsBytes(RuntimeDescriptorTable.Build([], []).AsSpan()));
        for (var round = 0; round < 8 && scenario.Registry.Images.Count < Count; round++)
        {
            scenario.Round(sampler, table, out table);
        }

        Assert.Equal(Count, scenario.Registry.Images.Count);
        var registered = scenario.Registry.Images.Values.Select(entry => (entry.Key, entry.Slot)).ToList();
        // The S# words are zero (nearest, clamp): the one sampler the rounds registered.
        List<(uint[] Key, uint Slot)> samplers = [(new uint[RuntimeDescriptorTable.SamplerWordCount], scenario.Heap.GetOrCreateSamplerSlot(sampler))];

        // Twice as many entries as keys: 16 entries. Each key's home entry goes to a decoy of another
        // descriptor that hashes there and points at another image's slot.
        const uint Capacity = 16;
        var placed = new List<(uint[] Key, uint Slot)>();
        for (var index = 0; index < registered.Count; index++)
        {
            var key = registered[index].Key;
            var home = RuntimeDescriptorTable.Hash(key) & (Capacity - 1);
            var decoy = (uint[])key.Clone();
            for (decoy[^1] = 1; (RuntimeDescriptorTable.Hash(decoy) & (Capacity - 1)) != home; decoy[^1]++)
            {
            }

            placed.Add((decoy, registered[(index + 1) % registered.Count].Slot));
        }

        placed.AddRange(registered);
        var words = RuntimeDescriptorTable.Build(placed, samplers);
        Assert.Equal(Capacity, words[RuntimeDescriptorTable.ImageCapacityDword]);
        table = scenario.Runner.CreateBuffer(MemoryMarshal.AsBytes(words.AsSpan()));
        scenario.Round(sampler, table, out _);
        for (var index = 0; index < Count; index++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[index]), scenario.ResultWord(index));
        }

        Assert.Equal(0, scenario.LastImageMisses);
        scenario.Harness.AssertNoValidationMessages();
    }

    // The host registers descriptors, grows the table and frees heap slots between the draws of one command buffer
    // that is still recording. None of that may invalidate the buffer: the heap is update-after-bind, and a
    // superseded table or a read miss buffer is only destroyed once the submission that bound it has completed.
    [Fact]
    public void RegisteringAndGrowingTheTableWhileRecording_KeepsTheCommandBufferValid()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        const int Count = 3;
        using var scenario = new Scenario(vulkan, LoopProgram("ImageSampleLz", EntryBytes, (uint)Count, withSampler: true), EntryBytes * (uint)Count);
        var tableData = new byte[EntryBytes * (uint)Count];
        for (var index = 0; index < Count; index++)
        {
            var word = (uint)(ImageBase >> 8) + (uint)index * 0x10;
            WriteEntry(tableData, index, EntryBytes, ImageWords((ulong)word << 8, Format32Float));
            scenario.AddImage(word, Format.R32Sfloat, GuestPixelFormat.Bits32Float, Floats(0.25f * (index + 1)));
        }

        scenario.TableBytes = scenario.Runner.CreateBuffer(tableData);
        var sampler = scenario.Runner.CreateSampler(Filter.Nearest);
        List<RuntimeDescriptorBuffers> recording = [];
        var buffers = new RuntimeDescriptorBuffers(scenario.Harness.Device, scenario.Harness.Scheduler, recording);
        scenario.Harness.Run(() =>
        {
            var command = new CommandBuffer(scenario.Harness.Scheduler.Current.Handle);
            foreach (var image in scenario.Images)
            {
                image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            }

            // One tick, several dispatches: each registers one more descriptor, so the table is rebuilt (the
            // previous one superseded) and a heap slot is written while the earlier dispatches are recorded.
            for (var step = 0; step <= Count; step++)
            {
                if (step > 0)
                {
                    var (key, entry) = (step - 1, new RuntimeImageEntry<object>(ImageKeyOf(scenario, step - 1)));
                    scenario.Registry.Images.Add(RuntimeDescriptorKey.From(entry.Key), entry);
                    var (image, view) = scenario.ImagesByAddressWord[entry.Key[1]];
                    entry.Slot = scenario.Heap.GetOrCreateImageSlot(image.GetOrCreateView(view), ImageLayout.ShaderReadOnlyOptimal);
                    scenario.Registry.TableDirty = true;
                }

                var table = buffers.Table(scenario.Registry);
                var misses = buffers.Misses(scenario.Registry);
                scenario.Runner.Dispatch(scenario.Registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
                {
                    [DescriptorBindingKind.RuntimeDescriptorTable] = [table],
                    [DescriptorBindingKind.RuntimeDescriptorMisses] = [misses],
                    [DescriptorBindingKind.Buffers] = scenario.Snapshot.Buffers.Select(descriptor => descriptor[0] == (uint)TableAddress ? scenario.TableBytes! : scenario.Result).ToArray(),
                }, 1, flattenedTable: scenario.Snapshot.FlattenedResourceTable, heapSet: scenario.Heap.Set);
            }

            // The tick is about to be submitted: the miss buffer the dispatches wrote is copied out and cleared.
            foreach (var pending in recording)
            {
                pending.RecordMissCopies();
            }

            recording.Clear();
        });

        scenario.Harness.AssertNoValidationMessages();
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.25f), scenario.ResultWord(0));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.75f), scenario.ResultWord(2));
        scenario.Harness.Finish();
        // The first dispatch ran with an empty table: its misses came back through the device-local buffer.
        Assert.True(scenario.Registry.PendingMisses > 0);
        buffers.Dispose();
        scenario.Harness.AssertNoValidationMessages();
    }

    private static uint[] ImageKeyOf(Scenario scenario, int index) =>
        [RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D), (uint)(ImageBase >> 8) + (uint)index * 0x10, Format32Float << 20, 0, 0xFAC | (ImageType2D << 28), 0, 0, 0, 0];

    [Fact]
    public void ImageLoadLoop_FetchesThroughTheViewOfEachNumericClass()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        const int Count = 3;
        const uint LoadEntryBytes = 32;
        using var scenario = new Scenario(vulkan, LoopProgram("ImageLoad", LoadEntryBytes, (uint)Count, withSampler: false), LoadEntryBytes * (uint)Count);
        var tableData = new byte[LoadEntryBytes * (uint)Count];
        (uint GuestFormat, Format Host, GuestPixelFormat Guest, byte[] Texel, uint Expected)[] images =
        [
            (Format32Uint, Format.R32Uint, GuestPixelFormat.Bits32UInt, BitConverter.GetBytes(7u), 7u),
            (Format32Sint, Format.R32Sint, GuestPixelFormat.Bits32SInt, BitConverter.GetBytes(-3), unchecked((uint)-3)),
            (Format32Float, Format.R32Sfloat, GuestPixelFormat.Bits32Float, Floats(1.5f), BitConverter.SingleToUInt32Bits(1.5f)),
        ];
        for (var index = 0; index < Count; index++)
        {
            var word = (uint)(ImageBase >> 8) + (uint)index * 0x10;
            WriteEntry(tableData, index, LoadEntryBytes, ImageWords((ulong)word << 8, images[index].GuestFormat));
            scenario.AddImage(word, images[index].Host, images[index].Guest, images[index].Texel);
        }

        scenario.TableBytes = scenario.Runner.CreateBuffer(tableData);
        var sampler = scenario.Runner.CreateSampler(Filter.Nearest);
        var table = scenario.Runner.CreateBuffer(MemoryMarshal.AsBytes(RuntimeDescriptorTable.Build([], []).AsSpan()));

        scenario.Round(sampler, table, out table);
        for (var round = 0; round < 6 && scenario.Registry.Images.Count < Count; round++)
        {
            scenario.Round(sampler, table, out table);
        }

        Assert.Equal(Count, scenario.Registry.Images.Count);
        // The three descriptors differ in their view class: a uint, a sint and a float view of the heap.
        Assert.Equal(
            [ImageNumericClass.Float, ImageNumericClass.Uint, ImageNumericClass.Sint],
            scenario.Registry.Images.Values.Select(entry => entry.NumericClass).OrderBy(static value => value).ToArray());
        scenario.Round(sampler, table, out _);
        for (var index = 0; index < Count; index++)
        {
            Assert.Equal(images[index].Expected, scenario.ResultWord(index));
        }

        Assert.Equal(0, scenario.LastImageMisses);
        scenario.Harness.AssertNoValidationMessages();
        output.WriteLine($"Verified runtime image loads of three numeric views on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }
}
