// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Copies guest bytes only when they are already resident on the CPU side. It never
// synchronizes: a range the GPU may still own, or that a clean read would refuse,
// returns false so the caller falls back to a full materialization.
public delegate bool ResidentGuestBytesReader(ulong address, Span<byte> destination, bool clean);

// Materialization is a pure function of the plan, the user data words the evaluator
// reads, the shader base, the compute state and the guest words it reads. This cache
// records exactly those words and reuses the result while every one of them is
// unchanged, so a draw that re-binds the same resources skips the descriptor walk. A user
// data word the evaluator never read (a per-draw constant for the shader's own ALU, say)
// does not distinguish two draws; the snapshot a hit returns carries the current user
// data. Any failed read, any GPU-owned range and any byte difference make the entry
// miss; nothing is guessed.
public sealed class ResourceMaterializationCache
{
    // Two generations approximate LRU: a full young generation becomes the old one,
    // and an old entry that is used again is promoted back.
    private readonly int _generationCapacity;
    private Dictionary<ulong, Entry> _young = new();
    private Dictionary<ulong, Entry> _old = new();
    private byte[] _scratch = new byte[256];
    private ReadRecorder? _spareRecorder;
    // The user data words each (plan, base, compute state) has read so far. They only index the
    // entries; Entry.Matches is what decides a hit.
    private readonly Dictionary<ulong, ulong[]> _learnedUserData = new();
    private readonly Dictionary<ulong, AddressRebase> _rebases = new();

    private AddressRebase RebaseOf(ulong drawKey, ShaderResourcePlan plan)
    {
        if (!_rebases.TryGetValue(drawKey, out var rebase))
            _rebases.Add(drawKey, rebase = new AddressRebase(plan));
        return rebase;
    }

    // Titles allocate per-draw constants linearly, so a buffer's base address changes on every
    // draw while everything else the draw binds stays the same; keyed on that address, the cache
    // misses nearly every draw. A user data word the plan uses verbatim as a buffer's base dword
    // is a candidate. It is rebased only once misses have shown it repeatedly that two draws
    // differing in nothing but candidate words materialize identically, except for those base
    // dwords, which equal the words: the same descriptors, table, ranges, specialization and
    // recorded guest bytes. One counterexample rejects the word for good. A rebased word leaves
    // the key and the match, and a hit patches the base dwords from the draw's own user data.
    private sealed class AddressRebase
    {
        private const int Confirmations = 3;

        // A word that is a base address. A buffer candidate is the base dword of a buffer descriptor
        // (Buffer >= 0): the hit patches that dword, and reads inside the descriptor's range move.
        // A raw candidate is the low dword of the handle of scalar loads; its reads lie at constant
        // byte offsets [MinOffset, MaxOffset] from the base whose high dword is user data word High
        // (or HighConstant when High < 0).
        private readonly record struct Candidate(int UserData, int Buffer, int High = -1, uint HighConstant = 0,
            long MinOffset = 0, long MaxOffset = 0);

        private const ulong RawAddressMask = 0x0000_FFFF_FFFF_FFFFul;

        private readonly Candidate[] _candidates;
        private readonly ulong[] _candidateMask;
        private ulong[] _rejected = [];
        private readonly Dictionary<int, int> _confirmed = new();
        private const int RecentCapacity = 512;
        private readonly Dictionary<ulong, Entry> _recent = new();

        private ulong StructureOf(Entry entry)
        {
            var hash = new HashCode();
            hash.Add(entry.UserData.Length);
            for (var index = 0; index < entry.UserData.Length; index++)
            {
                if (!IsUsed(entry.UsedUserData, index) || IsUsed(_candidateMask, index))
                    continue;
                hash.Add(index);
                hash.Add(entry.UserData[index]);
            }

            return (uint)hash.ToHashCode();
        }

        private readonly ShaderResourcePlan _plan;

        public AddressRebase(ShaderResourcePlan plan)
        {
            _plan = plan;
            var candidates = new List<Candidate>();

            for (var buffer = 0; buffer < plan.Info.Buffers.Count; buffer++)
            {
                var source = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source];
                if (source.Dwords.Length != 0 && UserDataIndex(source.Dwords[0]) is { } index)
                    candidates.Add(new Candidate(index, buffer));
            }

            var values = PlanValues(plan);
            var buffered = candidates.Select(candidate => candidate.UserData).ToHashSet();
            foreach (var raw in RawCandidates(plan, values))
                if (!buffered.Contains(raw.UserData))
                    candidates.Add(raw);

            _candidates = candidates.ToArray();
            _candidateMask = MaskOf(_candidates.Select(candidate => candidate.UserData));
            var memoryBases = MemoryBaseCandidates(plan);
            MemoryBases = memoryBases.Select(candidate => candidate.Buffer).ToArray();
            _memoryState = new int[plan.Info.Buffers.Count];
            _memorySlots = new int[plan.Info.Buffers.Count];
            _memorySlots.AsSpan().Fill(-1);
            foreach (var (buffer, slot) in memoryBases)
                _memorySlots[buffer] = slot;
            _memoryLearnable = memoryBases.Count;
            (TableSlotBuffers, TableSlotOffsets, TableSlotsThroughBases) = TableSlotSources(plan, memoryBases);
            DirectSlots = DirectSlotsOf(plan, TableSlotBuffers);
        }

        // A table slot that is one dword loaded through a user data pointer at constant offsets: its
        // word is that dword. High is the user data index of the pointer's high dword, or -1 for
        // the constant HighConstant.
        public readonly record struct DirectLoad(int Low, int High, ulong HighConstant, long Immediate, uint Offset);

        // Per table slot: its direct load (default for a slot through a memory-based buffer), or
        // null when some other slot is not a direct load.
        public DirectLoad[]? DirectSlots { get; }

        private DirectLoad[]? DirectSlotsOf(ShaderResourcePlan plan, int[] slotBuffers)
        {
            var loads = new DirectLoad[plan.TableReads.Count];
            for (var slot = 0; slot < loads.Length; slot++)
            {
                if (slotBuffers[slot] >= 0)
                    continue;
                var value = plan.TableReads[slot].Value;
                if (value.Kind != ScalarValueKind.ScalarAddressWord || value.Operands.Length < 2 ||
                    value.Operands[1].Kind != ScalarValueKind.Constant || (uint)value.MemoryIndex >= (uint)plan.Memory.Count)
                    return null;
                var handle = value.Operands[0];
                if (handle.Operands.Length < 2 || UserDataIndex(handle.Operands[0]) is not { } low)
                    return null;
                var high = UserDataIndex(handle.Operands[1]);
                if (high is null && handle.Operands[1].Kind != ScalarValueKind.Constant)
                    return null;
                loads[slot] = new DirectLoad(low, high ?? -1, high is null ? handle.Operands[1].Payload : 0,
                    (int)plan.Memory[value.MemoryIndex].Offset, (uint)value.Operands[1].Payload);
            }

            return loads;
        }

        // Per table slot: the memory-based buffer it reads a dword of at a constant byte offset from
        // the base, or -1. TableSlotsThroughBases: some slot reads through such a buffer otherwise.
        public int[] TableSlotBuffers { get; } = [];
        public long[] TableSlotOffsets { get; } = [];
        public bool TableSlotsThroughBases { get; }

        private static (int[] Buffers, long[] Offsets, bool Through) TableSlotSources(ShaderResourcePlan plan,
            List<(int Buffer, int Slot)> memoryBases)
        {
            var buffers = new int[plan.TableReads.Count];
            var offsets = new long[plan.TableReads.Count];
            buffers.AsSpan().Fill(-1);
            if (memoryBases.Count == 0)
                return (buffers, offsets, false);

            int BufferOf(ScalarValue handleWord)
            {
                foreach (var (buffer, slot) in memoryBases)
                {
                    var baseWord = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source].Dwords[0];
                    if (ReferenceEquals(handleWord, baseWord) ||
                        (slot >= 0 && handleWord.Kind == ScalarValueKind.ResourceTableWord && (int)handleWord.Payload == slot))
                        return buffer;
                }

                return -1;
            }

            var through = false;
            for (var slot = 0; slot < plan.TableReads.Count; slot++)
            {
                var value = plan.TableReads[slot].Value;
                if (value.Kind == ScalarValueKind.ScalarBufferWord && value.Operands.Length >= 2 &&
                    value.Operands[0].Operands.Length != 0 && value.Operands[1].Kind == ScalarValueKind.Constant &&
                    value.MemoryIndex >= 0 && value.MemoryIndex < plan.Memory.Count &&
                    BufferOf(value.Operands[0].Operands[0]) is var direct and >= 0)
                {
                    buffers[slot] = direct;
                    offsets[slot] = ((long)plan.Memory[value.MemoryIndex].Offset + (long)(uint)value.Operands[1].Payload) & ~3L;
                    continue;
                }

                var visited = new HashSet<ScalarValue>();
                var pending = new Stack<ScalarValue>();
                pending.Push(value);
                while (!through && pending.Count != 0)
                {
                    var current = pending.Pop();
                    if (!visited.Add(current))
                        continue;
                    if (current.Kind == ScalarValueKind.ScalarBufferWord && current.Operands.Length != 0 &&
                        current.Operands[0].Operands.Length != 0 && BufferOf(current.Operands[0].Operands[0]) >= 0)
                        through = true;
                    foreach (var operand in current.Operands)
                        pending.Push(operand);
                }
            }

            return (buffers, offsets, through);
        }

        private readonly int[] _memorySlots;

        // Memory-loaded bases, learned per buffer like the user data words: confirmations count up
        // to MemoryActive; MemoryRejected is final.
        private const int MemoryActive = Confirmations;
        private const int MemoryRejected = -1;
        private readonly int[] _memoryState;
        private int _memoryLearnable;

        public IReadOnlyList<int> MemoryBases { get; }

        public bool HasLearnableMemoryBases => _memoryLearnable != 0;


        public bool IsRejectedMemoryBase(int buffer) => _memoryState[buffer] == MemoryRejected;

        public bool AreActiveMemoryBases(List<(int Buffer, uint Word)> patches)
        {
            foreach (var (buffer, _) in patches)
                if (_memoryState[buffer] < MemoryActive)
                    return false;
            return true;
        }

        // A rebased refresh computed for a stale entry, compared with the full materialization of
        // the same draw: agreement counts towards activating its bases, one disagreement rejects
        // them for good.
        public void ConfirmMemoryBases(List<(int Buffer, uint Word)> patches, bool agrees)
        {
            foreach (var (buffer, _) in patches)
            {
                if (_memoryState[buffer] == MemoryRejected)
                    continue;
                if (!agrees)
                {
                    _memoryState[buffer] = MemoryRejected;
                    _memoryLearnable--;
                }
                else if (_memoryState[buffer] < MemoryActive)
                {
                    _memoryState[buffer]++;
                }
            }
        }

        // The window of buffer reads a patched base covers for this draw.
        public static bool TryMovedWindow(uint[] descriptor, uint baseWord, out ulong start, out ulong size)
        {
            start = size = 0;
            if (descriptor.Length < 3)
                return false;
            start = baseWord | ((ulong)(descriptor[1] & 0xFFFF) << 32);
            var stride = (descriptor[1] >> 16) & 0x3FFF;
            size = stride == 0 ? descriptor[2] : (ulong)descriptor[2] * stride;
            return true;
        }

        // The base dword moves, and so does the table slot that holds the same word.
        public ResourceSnapshot PatchMemoryBases(ResourceSnapshot snapshot, List<(int Buffer, uint Word)> patches)
        {
            var buffers = (uint[][])snapshot.Buffers.Clone();
            uint[]? table = null;
            foreach (var (buffer, word) in patches)
            {
                if (ReferenceEquals(buffers[buffer], snapshot.Buffers[buffer]))
                    buffers[buffer] = (uint[])buffers[buffer].Clone();
                var previous = buffers[buffer][0];
                buffers[buffer][0] = word;
                var slot = _memorySlots[buffer];
                var flat = slot >= 0 && slot < _plan.TableReads.Count ? (int)_plan.TableReads[slot].FlatOffset : -1;
                if (flat >= 0 && flat < snapshot.FlattenedResourceTable.Length && snapshot.FlattenedResourceTable[flat] == previous)
                {
                    table ??= (uint[])snapshot.FlattenedResourceTable.Clone();
                    table[flat] = word;
                }
            }

            return new ResourceSnapshot
            {
                Buffers = buffers,
                Images = snapshot.Images,
                Samplers = snapshot.Samplers,
                FlattenedResourceTable = table ?? snapshot.FlattenedResourceTable,
                UserData = snapshot.UserData,
                DeviceAddressRanges = snapshot.DeviceAddressRanges,
            };
        }

        private int? UserDataIndex(ScalarValue value)
        {
            if (value.Kind != ScalarValueKind.UserData)
                return null;
            var register = value.UserDataRegister;
            return register >= _plan.UserDataBase && register - _plan.UserDataBase < _plan.UserDataCount
                ? (int)(register - _plan.UserDataBase)
                : null;
        }

        // Every scalar load of the plan whose handle takes its low dword verbatim from user data.
        // A word qualifies only when all its loads use constant offsets and one high dword.
        // Every value the plan evaluates, operands included.
        private static HashSet<ScalarValue> PlanValues(ShaderResourcePlan plan)
        {
            var roots = new List<ScalarValue>();
            foreach (var source in plan.DescriptorSources)
                roots.AddRange(source.Dwords);
            roots.AddRange(plan.TableReads.Select(read => read.Value));
            roots.AddRange(plan.DynamicReads);
            foreach (var access in plan.Accesses)
            {
                if (access is null)
                    continue;
                foreach (var value in new[] { access.Handle, access.SamplerHandle, access.Read, access.Offset, access.Active })
                    if (value is not null)
                        roots.Add(value);
            }

            roots.AddRange(plan.Graph.Values);
            var visited = new HashSet<ScalarValue>();
            var pending = new Stack<ScalarValue>(roots);
            while (pending.Count != 0)
            {
                var value = pending.Pop();
                if (!visited.Add(value))
                    continue;
                foreach (var operand in value.Operands)
                    pending.Push(operand);
            }

            return visited;
        }

        // Buffers whose base dword is read from guest memory, directly or through a slot of the
        // flattened table: descriptor tables the title rewrites per frame or per draw point them at
        // freshly allocated constants. The slot (or -1) is where the table holds the same word.
        // Recorded reads inside such a buffer's window move with its base.
        private static List<(int Buffer, int Slot)> MemoryBaseCandidates(ShaderResourcePlan plan)
        {
            var bases = new List<(int Buffer, int Slot)>();
            for (var buffer = 0; buffer < plan.Info.Buffers.Count; buffer++)
            {
                var source = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source];
                if (source.Dwords.Length < 2)
                    continue;
                var baseWord = source.Dwords[0];
                var slot = baseWord.Kind == ScalarValueKind.ResourceTableWord ? (int)baseWord.Payload : -1;
                if (slot < 0 && baseWord.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord))
                    continue;
                bases.Add((buffer, slot));
            }

            return bases;
        }

        private IEnumerable<Candidate> RawCandidates(ShaderResourcePlan plan, HashSet<ScalarValue> values)
        {
            var found = new Dictionary<int, Candidate>();
            var disqualified = new HashSet<int>();
            foreach (var value in values)
            {
                if (value.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord) ||
                    value.Operands.Length < 2 || value.Operands[0].Operands.Length < 2 ||
                    UserDataIndex(value.Operands[0].Operands[0]) is not { } index)
                    continue;

                var high = value.Operands[0].Operands[1];
                var highIndex = UserDataIndex(high);
                var offset = value.Operands[1];
                if ((highIndex is null && high.Kind != ScalarValueKind.Constant) || offset.Kind != ScalarValueKind.Constant ||
                    value.MemoryIndex >= plan.Memory.Count)
                {
                    disqualified.Add(index);
                    continue;
                }

                var immediate = (long)plan.Memory[value.MemoryIndex].Offset;
                var relative = value.Kind == ScalarValueKind.ScalarBufferWord
                    ? (immediate + (long)(uint)offset.Payload) & ~3L
                    : (immediate & ~3L) + (long)((uint)offset.Payload & ~3u);
                var candidate = new Candidate(index, -1, highIndex ?? -1, highIndex is null ? (uint)high.Payload : 0, relative, relative);
                if (found.TryGetValue(index, out var existing))
                {
                    if (existing.High != candidate.High || existing.HighConstant != candidate.HighConstant)
                    {
                        disqualified.Add(index);
                        continue;
                    }

                    candidate = existing with
                    {
                        MinOffset = Math.Min(existing.MinOffset, relative),
                        MaxOffset = Math.Max(existing.MaxOffset, relative),
                    };
                }

                found[index] = candidate;
            }

            return found.Values.Where(candidate => !disqualified.Contains(candidate.UserData));
        }

        // The window [start, start + size) of reads a candidate's base covers in this entry.
        private bool TryWindow(Entry entry, in Candidate candidate, out ulong start, out ulong size, out ulong addressBase)
        {
            start = size = addressBase = 0;
            if (candidate.Buffer >= 0)
            {
                if (candidate.Buffer >= entry.Snapshot.Buffers.Length)
                    return false;
                var descriptor = entry.Snapshot.Buffers[candidate.Buffer];
                if (descriptor.Length < 3)
                    return false;
                start = addressBase = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
                var stride = (descriptor[1] >> 16) & 0x3FFF;
                size = stride == 0 ? descriptor[2] : (ulong)descriptor[2] * stride;
                return true;
            }

            // The window starts at the base itself: the read prefetch fetches a pointer's block from
            // its base, ahead of the lowest offset a load uses.
            addressBase = RawBase(entry.UserData, candidate, entry.UserData[candidate.UserData]);
            var lowest = Math.Min(0, candidate.MinOffset);
            var low = (long)addressBase + lowest;
            if (low < 0)
                return false;
            start = (ulong)low;
            size = (ulong)(candidate.MaxOffset - lowest) + sizeof(uint);
            return true;
        }

        private static ulong RawBase(IReadOnlyList<uint> userData, in Candidate candidate, uint low)
        {
            var high = candidate.High >= 0 ? userData[candidate.High] : candidate.HighConstant;
            return (((ulong)high << 32) | low) & RawAddressMask & ~3UL;
        }

        // The rebased words; replaced, never mutated, so a caller can compare references.
        public ulong[] Active { get; private set; } = [];

        // Every rebased base dword of the entry equals the word it came from.
        public bool AppliesTo(Entry entry)
        {
            if (Active.Length == 0)
                return true;
            foreach (var (userData, buffer, _, _, _, _) in _candidates)
            {
                if (buffer >= 0 && IsUsed(Active, userData) &&
                    (buffer >= entry.Snapshot.Buffers.Length || entry.Snapshot.Buffers[buffer].Length == 0 ||
                     userData >= entry.UserData.Length || entry.Snapshot.Buffers[buffer][0] != entry.UserData[userData]))
                    return false;
            }

            return true;
        }

        public ResourceSnapshot Apply(ResourceSnapshot snapshot, IReadOnlyList<uint> userData)
        {
            if (Active.Length == 0)
                return snapshot;
            uint[][]? buffers = null;
            foreach (var (index, buffer, _, _, _, _) in _candidates)
            {
                if (buffer < 0 || !IsUsed(Active, index) || snapshot.Buffers[buffer][0] == userData[index])
                    continue;
                buffers ??= (uint[][])snapshot.Buffers.Clone();
                if (ReferenceEquals(buffers[buffer], snapshot.Buffers[buffer]))
                    buffers[buffer] = (uint[])buffers[buffer].Clone();
                buffers[buffer][0] = userData[index];
            }

            return buffers is null
                ? snapshot
                : new ResourceSnapshot
                {
                    Buffers = buffers,
                    Images = snapshot.Images,
                    Samplers = snapshot.Samplers,
                    FlattenedResourceTable = snapshot.FlattenedResourceTable,
                    UserData = snapshot.UserData,
                    DeviceAddressRanges = snapshot.DeviceAddressRanges,
                };
        }

        // Compares a fresh materialization with the latest one that agrees on every word but the
        // candidates: the same object drawn again, usually a frame earlier, with new constants.
        // Comparing with the plan's previous draw instead finds another object's textures in
        // the way and never concludes.
        public void Learn(Entry built,
            Func<Entry, ulong[], (ResourceSnapshot Snapshot, List<(int Buffer, uint Word)>? Patches)?>? predict = null)
        {
            if (_candidates.Length == 0)
                return;
            var structure = StructureOf(built);
            _recent.TryGetValue(structure, out var previous);
            if (_recent.Count >= RecentCapacity)
                _recent.Clear();
            _recent[structure] = built;
            if (previous is null || previous.UserData.Length != built.UserData.Length ||
                !previous.UsedUserData.AsSpan().SequenceEqual(built.UsedUserData))
                return;

            var differing = new List<int>();
            for (var index = 0; index < built.UserData.Length; index++)
            {
                if (!IsUsed(built.UsedUserData, index) || previous.UserData[index] == built.UserData[index])
                    continue;
                if (!IsUsed(_candidateMask, index))
                    return;
                differing.Add(index);
            }

            // A word already rejected explains any mismatch; blaming the others with it would reject
            // sound bases that merely change together with it.
            if (differing.Count == 0 || differing.Any(index => IsUsed(_rejected, index)))
                return;

            // Reads must land where the previous draw's did, moved with a rebased buffer when they
            // fall inside its window. Guest bytes that changed between the two draws make the
            // comparison say nothing about the words.
            var reads = CompareMovedReads(this, previous, built, differing);
            if (reads == ReadComparison.Inconclusive)
            {
                // Changed guest bytes hide whether the words are bases. Predict this draw from the
                // previous one as a hit with the words rebased would, moved memory-loaded bases
                // included, and judge the words by the prediction instead.
                if (predict?.Invoke(previous, Or(Active, MaskOf(differing))) is not { } prediction)
                    return;

                var agrees = Difference(prediction.Snapshot, previous.Specialization, built.Snapshot, built.Specialization) is null;
                if (prediction.Patches is { } patches)
                {
                    // A wrong prediction that moved bases blames those bases, not the words.
                    ConfirmMemoryBases(patches, agrees);
                    if (!agrees)
                        return;
                }
                else if (!agrees)
                {
                    _rejected = Or(_rejected, MaskOf(differing));
                    if (Overlaps(Active, _rejected))
                        Active = AndNot(Active, _rejected);
                    return;
                }

                Confirm(differing);
                return;
            }

            if (reads == ReadComparison.Mismatch ||
                !SameExceptBases(previous, built, differing, compareTable: reads == ReadComparison.SameBytes))
            {
                _rejected = Or(_rejected, MaskOf(differing));
                if (Overlaps(Active, _rejected))
                    Active = AndNot(Active, _rejected);
                return;
            }

            Confirm(differing);
        }

        private void Confirm(List<int> differing)
        {
            var activated = new List<int>();
            foreach (var index in differing)
            {
                var count = _confirmed.GetValueOrDefault(index) + 1;
                _confirmed[index] = count;
                if (count >= Confirmations && !IsUsed(_rejected, index) && !IsUsed(Active, index))
                    activated.Add(index);
            }

            if (activated.Count != 0)
                Active = Or(Active, MaskOf(activated));
        }

        // Runs a prediction as though the words in probe were rebased already.
        public T WithActive<T>(ulong[] probe, Func<T> run)
        {
            var saved = Active;
            Active = probe;
            try
            {
                return run();
            }
            finally
            {
                Active = saved;
            }
        }

        // The flattened table is left out when only table-only bytes changed: it holds the words read
        // from memory, whose addresses the read comparison already checked.
        private bool SameExceptBases(Entry previous, Entry built, List<int> differing, bool compareTable)
        {
            if (!previous.Specialization.Equals(built.Specialization))
                return false;

            var left = previous.Snapshot;
            var right = built.Snapshot;
            if (!SameWords(left.Images, right.Images) || !SameWords(left.Samplers, right.Samplers) ||
                (compareTable
                    ? !left.FlattenedResourceTable.AsSpan().SequenceEqual(right.FlattenedResourceTable)
                    : left.FlattenedResourceTable.Length != right.FlattenedResourceTable.Length) ||
                !left.DeviceAddressRanges.AsSpan().SequenceEqual(right.DeviceAddressRanges) ||
                left.Buffers.Length != right.Buffers.Length)
                return false;

            for (var buffer = 0; buffer < left.Buffers.Length; buffer++)
            {
                var before = left.Buffers[buffer];
                var after = right.Buffers[buffer];
                if (before.Length != after.Length)
                    return false;
                for (var dword = 0; dword < before.Length; dword++)
                {
                    if (dword == 0 && BaseWord(buffer, differing) is { } index)
                    {
                        if (before[0] != previous.UserData[index] || after[0] != built.UserData[index])
                            return false;
                    }
                    else if (before[dword] != after[dword])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Every read of the built draw is the previous draw's read, moved by the rebase when it lies
        // in a moved buffer's window; bytesDiffer tells whether the guest bytes read there changed.
        private enum ReadComparison
        {
            // A read moved where the words cannot explain it while every byte read alike matched.
            Mismatch,
            // Guest bytes changed where the descriptors depend on them, or a changed pointer in
            // guest memory redirected later reads: the pair says nothing about the words.
            Inconclusive,
            SameBytes,
            // Only words the flattened table alone reads changed: per-draw constants.
            TableBytesDiffer,
        }

        private static ReadComparison CompareMovedReads(AddressRebase rebase, Entry previous, Entry built, List<int> differing)
        {
            var matched = previous.RangeAddresses.Length == built.RangeAddresses.Length;
            var bytesDiffer = false;
            var descriptorBytesDiffer = false;
            var common = Math.Min(previous.RangeAddresses.Length, built.RangeAddresses.Length);
            for (var range = 0; range < common; range++)
            {
                var length = previous.RangeLengths[range];
                if (rebase.Move(previous, built.UserData, previous.RangeAddresses[range], length, differing) != built.RangeAddresses[range] ||
                    length != built.RangeLengths[range] || previous.RangeClean[range] != built.RangeClean[range])
                {
                    matched = false;
                    continue;
                }

                for (var offset = 0; offset < length; offset += sizeof(uint))
                {
                    var before = previous.RangeOffsets[range] + offset;
                    var after = built.RangeOffsets[range] + offset;
                    if (previous.Bytes.AsSpan(before, sizeof(uint)).SequenceEqual(built.Bytes.AsSpan(after, sizeof(uint))))
                        continue;
                    bytesDiffer = true;
                    descriptorBytesDiffer |= !previous.WordTableOnly[before / sizeof(uint)] || !built.WordTableOnly[after / sizeof(uint)];
                }
            }

            if (!matched)
                return bytesDiffer ? ReadComparison.Inconclusive : ReadComparison.Mismatch;
            if (descriptorBytesDiffer || (bytesDiffer && !(previous.TableRefreshable && built.TableRefreshable)))
                return ReadComparison.Inconclusive;
            return bytesDiffer ? ReadComparison.TableBytesDiffer : ReadComparison.SameBytes;
        }

        // Where a read the entry recorded lies for a draw with this user data: a read inside the
        // window of a candidate whose base word moved moves with it. Only the words in moving count
        // (the differing words, or the rebased ones on a hit).
        private ulong Move(Entry entry, IReadOnlyList<uint> userData, ulong address, int length, IReadOnlyCollection<int>? moving)
        {
            foreach (var candidate in _candidates)
            {
                var index = candidate.UserData;
                if (moving is null ? !IsUsed(Active, index) : !moving.Contains(index))
                    continue;
                if (userData[index] == entry.UserData[index] || !TryWindow(entry, candidate, out var start, out var size, out var addressBase))
                    continue;
                // Only a range wholly inside the window moves: a range that merely overlaps it can hold
                // another block the title allocated next to it, which must not move.
                if (address < start || address - start + (ulong)length > size)
                    continue;
                var movedBase = candidate.Buffer >= 0
                    ? (addressBase & ~0xFFFF_FFFFUL) | userData[index]
                    : RawBase(entry.UserData, candidate, userData[index]);
                return address - addressBase + movedBase;
            }

            return address;
        }

        // A hit reads the entry's recorded ranges where they lie for this draw.
        public ulong MoveRead(Entry entry, IReadOnlyList<uint> userData, ulong address, int length) =>
            Active.Length == 0 ? address : Move(entry, userData, address, length, null);

        // True when a rebased base of the entry differs from this draw's word.
        public bool Moves(Entry entry, IReadOnlyList<uint> userData)
        {
            foreach (var candidate in _candidates)
                if (IsUsed(Active, candidate.UserData) && entry.UserData[candidate.UserData] != userData[candidate.UserData])
                    return true;
            return false;
        }

        private int? BaseWord(int buffer, List<int> differing)
        {
            foreach (var candidate in _candidates)
                if (candidate.Buffer == buffer && differing.Contains(candidate.UserData))
                    return candidate.UserData;
            return null;
        }

        private static bool SameWords(uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!left[index].AsSpan().SequenceEqual(right[index]))
                    return false;
            return true;
        }

        private static ulong[] MaskOf(IEnumerable<int> indices)
        {
            var mask = Array.Empty<ulong>();
            foreach (var index in indices)
            {
                if (mask.Length <= index >> 6)
                    Array.Resize(ref mask, (index >> 6) + 1);
                mask[index >> 6] |= 1UL << (index & 63);
            }

            return mask;
        }

        private static ulong[] Or(ulong[] left, ulong[] right)
        {
            var result = new ulong[Math.Max(left.Length, right.Length)];
            for (var index = 0; index < result.Length; index++)
                result[index] = (index < left.Length ? left[index] : 0) | (index < right.Length ? right[index] : 0);
            return result;
        }

        private static ulong[] AndNot(ulong[] left, ulong[] right)
        {
            var result = (ulong[])left.Clone();
            for (var index = 0; index < result.Length && index < right.Length; index++)
                result[index] &= ~right[index];
            return result.All(word => word == 0) ? [] : result;
        }

        private static bool Overlaps(ulong[] left, ulong[] right)
        {
            for (var index = 0; index < left.Length && index < right.Length; index++)
                if ((left[index] & right[index]) != 0)
                    return true;
            return false;
        }
    }

    public ResourceMaterializationCache(int generationCapacity = 16384)
    {
        _generationCapacity = Math.Max(1, generationCapacity);
    }

    private static long _totalHits;
    private static long _totalMisses;
    private static long _totalUncacheable;
    private const int MaxVariants = 8;
    private static long _totalStale;
    private static long _totalStaleUnreadable;
    private static long _totalRefreshes;


    [ThreadStatic]
    private static bool _readingTable;

    public static bool ReadingTable
    {
        get => _readingTable;
        private set => _readingTable = value;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Uncacheable { get; private set; }
    public long TableRefreshes { get; private set; }

    // Process-wide counters since the previous call, for the periodic render report.
    public static string TakeReport()
    {
        var hits = Interlocked.Exchange(ref _totalHits, 0);
        var misses = Interlocked.Exchange(ref _totalMisses, 0);
        var uncacheable = Interlocked.Exchange(ref _totalUncacheable, 0);
        var stale = Interlocked.Exchange(ref _totalStale, 0);
        var staleUnreadable = Interlocked.Exchange(ref _totalStaleUnreadable, 0);
        var refreshes = Interlocked.Exchange(ref _totalRefreshes, 0);
        var total = hits + misses;
        return FormattableString.Invariant(
            $"[PERF][RESOURCE_CACHE] hits={hits} misses={misses} stale={stale} stale_unreadable={staleUnreadable} refreshes={refreshes} uncacheable={uncacheable} hit_rate={(total == 0 ? 0 : hits * 100.0 / total):F1}% {RawReadPrefetch.TakeReport()}");
    }

    // The first part a cached result differs from a fresh one in, or null when they are the same.
    private static string? Difference(ResourceSnapshot cached, ResourceSpecialization cachedSpecialization,
        ResourceSnapshot fresh, ResourceSpecialization freshSpecialization)
    {
        static string? Words(string name, uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
                return $"{name}.count {left.Length}!={right.Length}";
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index].Length != right[index].Length)
                    return $"{name}[{index}].length";
                for (var word = 0; word < left[index].Length; word++)
                    if (left[index][word] != right[index][word])
                        return $"{name}[{index}][{word}] cached=0x{left[index][word]:X} fresh=0x{right[index][word]:X}";
            }

            return null;
        }

        if (!cachedSpecialization.Equals(freshSpecialization))
            return "specialization";
        if (Words("buffer", cached.Buffers, fresh.Buffers) is { } buffers)
            return buffers;
        if (Words("image", cached.Images, fresh.Images) is { } images)
            return images;
        if (Words("sampler", cached.Samplers, fresh.Samplers) is { } samplers)
            return samplers;
        if (cached.FlattenedResourceTable.Length != fresh.FlattenedResourceTable.Length)
            return "table.length";
        for (var slot = 0; slot < cached.FlattenedResourceTable.Length; slot++)
            if (cached.FlattenedResourceTable[slot] != fresh.FlattenedResourceTable[slot])
                return $"table[{slot}] cached=0x{cached.FlattenedResourceTable[slot]:X} fresh=0x{fresh.FlattenedResourceTable[slot]:X}";
        if (!cached.DeviceAddressRanges.AsSpan().SequenceEqual(fresh.DeviceAddressRanges))
            return "ranges";
        if (!cached.UserData.AsSpan().SequenceEqual(fresh.UserData))
            return "user_data";
        return null;
    }

    public bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        var drawKey = DrawKeyOf(plan, inputs);
        _learnedUserData.TryGetValue(drawKey, out var learned);
        var rebase = RebaseOf(drawKey, plan);
        var key = KeyOf(drawKey, inputs.UserData, learned, rebase.Active);
        var found = TryFind(key, plan, inputs, rebase.Active, out var cached);
        Entry? stale = null;
        (List<(int Buffer, uint Word)> Patches, ResourceSnapshot Snapshot, Entry From)? learning = null;
        if (found)
        {
            var unreadable = false;
            Entry? previous = null;
            for (var variant = cached; variant is not null; previous = variant, variant = variant.Next)
            {
                if (!variant.Matches(plan, inputs, rebase.Active) || !rebase.AppliesTo(variant))
                    continue;
                stale ??= variant;
                if (Validate(variant, residentReader, rebase, inputs.UserData, out var variantUnreadable))
                {
                    if (previous is not null)
                    {
                        previous.Next = variant.Next;
                        variant.Next = cached;
                        Store(key, variant);
                    }

                    Hits++;
                    Interlocked.Increment(ref _totalHits);
                    snapshot = rebase.Apply(WithCurrentUserData(variant.Snapshot, inputs.UserData), inputs.UserData);
                    specialization = variant.Specialization;
                    failure = default;
                    return true;
                }

                unreadable |= variantUnreadable;
            }

            // Moved bases first: a changed base word is a descriptor word, which a table refresh
            // would read in full only to refuse.
            ResourceSnapshot rebased = null!;
            List<(int Buffer, uint Word)> patches = null!;
            // Each variant is tried: a key's variants are often the same draw with different
            // descriptors (materials), and only the one this draw binds can be rebased.
            Entry? refreshedFrom = null;
            Entry? beforeRefreshed = null;
            if (stale is not null && rebase.HasLearnableMemoryBases)
            {
                for (Entry? previousVariant = null, variant = cached; variant is not null; previousVariant = variant, variant = variant.Next)
                {
                    if (!variant.Matches(plan, inputs, rebase.Active) || !rebase.AppliesTo(variant))
                        continue;
                    if (TryRebaseRefresh(variant, plan, inputs, residentReader, rebase, out rebased, out patches))
                    {
                        refreshedFrom = variant;
                        beforeRefreshed = previousVariant;
                        break;
                    }
                }
            }

            if (refreshedFrom is not null)
            {
                if (rebase.AreActiveMemoryBases(patches))
                {
                    if (beforeRefreshed is not null)
                    {
                        beforeRefreshed.Next = refreshedFrom.Next;
                        refreshedFrom.Next = cached;
                        Store(key, refreshedFrom);
                    }

                    Hits++;
                    Interlocked.Increment(ref _totalHits);
                    snapshot = rebased;
                    specialization = refreshedFrom.Specialization;
                    failure = default;
                    return true;
                }

                learning = (patches, rebased, refreshedFrom);
            }
            else if (rebase.AppliesTo(cached) && TryRefreshTable(key, cached, plan, inputs, residentReader, rebase, out var refreshed))
            {
                TableRefreshes++;
                Interlocked.Increment(ref _totalRefreshes);
                snapshot = rebase.Apply(WithCurrentUserData(refreshed.Snapshot, inputs.UserData), inputs.UserData);
                specialization = refreshed.Specialization;
                failure = default;
                return true;
            }

            Interlocked.Increment(ref _totalStale);
            if (unreadable)
                Interlocked.Increment(ref _totalStaleUnreadable);
        }

        Misses++;
        Interlocked.Increment(ref _totalMisses);
        var recorder = TakeRecorder();
        try
        {
            var recording = new ResourceRuntimeInputs
            {
                UserData = recorder.WrapUserData(inputs.UserData),
                ShaderBase = inputs.ShaderBase,
                ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
                TablePhase = recorder.TablePhase,
            };
            if (!ResourceMaterializer.Materialize(plan, recording, ref snapshot, ref specialization, out failure))
                return false;

            if (recorder.Failed)
            {
                Uncacheable++;
                Interlocked.Increment(ref _totalUncacheable);
                return true;
            }

            var built = recorder.Build(plan, inputs, snapshot, specialization, rebase.MemoryBases);
            if (learning is { } pending)
            {
                var agrees = Difference(pending.Snapshot, pending.From.Specialization, built.Snapshot, built.Specialization) is null;
                rebase.ConfirmMemoryBases(pending.Patches, agrees);
            }

            var activeBefore = rebase.Active;
            Learn(rebase, built, plan, inputs, residentReader);
            var widened = Union(learned, built.UsedUserData);
            if (!ReferenceEquals(widened, learned) || !ReferenceEquals(activeBefore, rebase.Active))
            {
                _learnedUserData[drawKey] = widened;
                key = KeyOf(drawKey, inputs.UserData, widened, rebase.Active);
                found = false;
            }

            if (found)
            {
                built.Next = cached;
                var depth = 1;
                for (var variant = built; variant.Next is not null; variant = variant.Next)
                {
                    if (++depth >= MaxVariants)
                    {
                        variant.Next = null;
                        break;
                    }
                }
            }

            Store(key, built);
            return true;
        }
        finally
        {
            ReturnRecorder(recorder);
        }
    }

    // Most stale entries in Demon's Souls differ only in words the shader reads through scalar
    // loads (inline constants rewritten every frame); the descriptors, device ranges and
    // specialization are unchanged. When every changed word was read only while evaluating the
    // flattened table, only the table is evaluated again. It is refused when the plan's
    // specialization reads or extends the table (indirect or candidate tables), when the table
    // now reads a word the entry did not validate, or when a word moved between the two reads.
    //
    // With rebased words the entry's ranges are read where they lie for this draw. The refreshed
    // entry keeps the entry's own addresses and base words with the bytes read there, so a later
    // draw moves its reads the same way and compares against those bytes.
    private bool TryRefreshTable(ulong key, Entry cached, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, AddressRebase rebase, out Entry refreshed)
    {
        refreshed = null!;
        if (!cached.TableRefreshable)
            return false;

        var current = new byte[cached.Bytes.Length];
        var moved = cached.RangeAddresses;
        if (rebase.Moves(cached, inputs.UserData))
        {
            moved = new ulong[cached.RangeAddresses.Length];
            for (var index = 0; index < moved.Length; index++)
                moved[index] = rebase.MoveRead(cached, inputs.UserData, cached.RangeAddresses[index], cached.RangeLengths[index]);
        }

        for (var index = 0; index < cached.RangeAddresses.Length; index++)
        {
            if (!residentReader(moved[index], current.AsSpan(cached.RangeOffsets[index], cached.RangeLengths[index]), cached.RangeClean[index]))
                return false;
        }

        var changed = false;
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            if (current.AsSpan(offset, sizeof(uint)).SequenceEqual(cached.Bytes.AsSpan(offset, sizeof(uint))))
                continue;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return false;
            changed = true;
        }

        if (!changed)
            return false;

        var recorder = TakeRecorder();
        try
        {
            var recording = new ResourceRuntimeInputs
            {
                UserData = inputs.UserData,
                ShaderBase = inputs.ShaderBase,
                ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
            };
            var cachedTable = cached.Snapshot.FlattenedResourceTable;
            ReadingTable = true;
            bool evaluated;
            uint[] table;
            try
            {
                evaluated = ResourceMaterializer.TryEvaluateTable(plan, recording, out table);
            }
            finally
            {
                ReadingTable = false;
            }

            if (!evaluated || recorder.Failed || table.Length != cachedTable.Length)
                return false;

            foreach (var (address, word, _, _) in recorder.Reads)
            {
                var found = ReferenceEquals(moved, cached.RangeAddresses)
                    ? TryFindWord(cached, address, out var offset)
                    : TryFindMovedWord(cached, moved, address, out offset);
                if (!found || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
                    return false;
            }

            // The written device-address slots come from reads validated unchanged above.
            foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
                cachedTable.AsSpan((int)slot, ShaderResourcePlan.WrittenRangeDwordCount).CopyTo(table.AsSpan((int)slot));

            var previous = cached.Snapshot;
            refreshed = new Entry
            {
                Plan = cached.Plan,
                UserData = cached.UserData,
                UsedUserData = cached.UsedUserData,
                ShaderBase = cached.ShaderBase,
                ComputeState = cached.ComputeState,
                RangeAddresses = cached.RangeAddresses,
                RangeOffsets = cached.RangeOffsets,
                RangeLengths = cached.RangeLengths,
                RangeClean = cached.RangeClean,
                WordTableOnly = cached.WordTableOnly,
                BaseWordBuffers = cached.BaseWordBuffers,
                TableRefreshable = true,
                Bytes = current,
                Snapshot = new ResourceSnapshot
                {
                    Buffers = previous.Buffers,
                    Images = previous.Images,
                    Samplers = previous.Samplers,
                    FlattenedResourceTable = table,
                    UserData = previous.UserData,
                    DeviceAddressRanges = previous.DeviceAddressRanges,
                },
                Specialization = cached.Specialization,
                Next = cached.Next,
            };
            Store(key, refreshed);
            return true;
        }
        finally
        {
            ReturnRecorder(recorder);
        }
    }

    private ReadRecorder TakeRecorder()
    {
        var recorder = _spareRecorder ?? new ReadRecorder();
        _spareRecorder = null;
        return recorder;
    }

    private void ReturnRecorder(ReadRecorder recorder)
    {
        recorder.Reset();
        _spareRecorder = recorder;
    }

    // The byte offset of a dword read at address when the entry's ranges lie at moved addresses.
    private static bool TryFindMovedWord(Entry entry, ulong[] moved, ulong address, out int offset)
    {
        // moved may be a reused buffer longer than the entry's ranges.
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (address < moved[index])
                continue;
            var delta = address - moved[index];
            if (delta % sizeof(uint) == 0 && delta < (ulong)entry.RangeLengths[index])
            {
                offset = entry.RangeOffsets[index] + (int)delta;
                return true;
            }
        }

        offset = 0;
        return false;
    }

    // The byte offset of a recorded dword in the entry's bytes, found by its address.
    private static bool TryFindWord(Entry entry, ulong address, out int offset)
    {
        offset = 0;
        var addresses = entry.RangeAddresses;
        int low = 0, high = addresses.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (addresses[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
            return false;
        var delta = address - addresses[found];
        if (delta % sizeof(uint) != 0 || delta >= (ulong)entry.RangeLengths[found])
            return false;
        offset = entry.RangeOffsets[found] + (int)delta;
        return true;
    }

    private static ulong DrawKeyOf(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(plan));
        hash.Add(inputs.ShaderBase);
        hash.Add(inputs.ComputeState);
        hash.Add(inputs.UserData.Count);
        return (ulong)(uint)hash.ToHashCode() | ((ulong)(uint)RuntimeHelpers.GetHashCode(plan) << 32);
    }

    // Only the words the draw's shader has been seen to read take part, less the rebased ones: the
    // key is an index, and Entry.Matches decides whether an entry is reusable.
    private static ulong KeyOf(ulong drawKey, IReadOnlyList<uint> userData, ulong[]? learned, ulong[]? rebased = null)
    {
        var low = (uint)drawKey;
        var high = (uint)(drawKey >> 32) ^ 0x9E3779B9u;
        if (learned is not null)
        {
            for (var index = 0; index < userData.Count; index++)
            {
                if (!IsUsed(learned, index) || (rebased is not null && IsUsed(rebased, index)))
                    continue;
                var word = userData[index];
                low = (low ^ word) * 0x01000193u;
                high = ((high + word) * 0x85EBCA6Bu) ^ (high >> 13);
            }
        }

        return ((ulong)high << 32) | low;
    }

    private static bool IsUsed(ulong[] mask, int index) =>
        (uint)(index >> 6) < (uint)mask.Length && (mask[index >> 6] & (1UL << (index & 63))) != 0;

    // The same array when nothing new was learned, so the caller can tell.
    private static ulong[] Union(ulong[]? learned, ulong[] used)
    {
        if (learned is null)
            return used;
        var changed = learned.Length < used.Length;
        for (var index = 0; !changed && index < used.Length; index++)
            changed = (used[index] & ~learned[index]) != 0;
        if (!changed)
            return learned;
        var merged = new ulong[Math.Max(learned.Length, used.Length)];
        for (var index = 0; index < merged.Length; index++)
            merged[index] = (index < learned.Length ? learned[index] : 0) | (index < used.Length ? used[index] : 0);
        return merged;
    }

    // A cached snapshot is shared by every draw that hits it, so it keeps the user data of the draw
    // that built it; a draw whose unread words differ gets its own copy that carries its own.
    private static ResourceSnapshot WithCurrentUserData(ResourceSnapshot cached, IReadOnlyList<uint> userData)
    {
        var words = cached.UserData;
        var same = words.Length == userData.Count;
        for (var index = 0; same && index < words.Length; index++)
            same = words[index] == userData[index];
        if (same)
            return cached;
        var current = new uint[userData.Count];
        for (var index = 0; index < current.Length; index++)
            current[index] = userData[index];
        return new ResourceSnapshot
        {
            Buffers = cached.Buffers,
            Images = cached.Images,
            Samplers = cached.Samplers,
            FlattenedResourceTable = cached.FlattenedResourceTable,
            UserData = current,
            DeviceAddressRanges = cached.DeviceAddressRanges,
        };
    }

    private bool TryFind(ulong key, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased, out Entry entry)
    {
        if (_young.TryGetValue(key, out entry!))
            return entry.Matches(plan, inputs, rebased);
        if (!_old.TryGetValue(key, out entry!) || !entry.Matches(plan, inputs, rebased))
            return false;
        _old.Remove(key);
        Store(key, entry);
        return true;
    }

    private void Store(ulong key, Entry entry)
    {
        if (_young.Count >= _generationCapacity && !_young.ContainsKey(key))
        {
            _old = _young;
            _young = new Dictionary<ulong, Entry>(_generationCapacity);
        }

        _young[key] = entry;
    }

    // What a hit on the previous entry would give this draw with the words in probe rebased: its
    // reads moved and its bytes unchanged, or its memory-loaded bases moved as well. Null when
    // neither path applies, which says nothing about the words.
    private (ResourceSnapshot Snapshot, List<(int Buffer, uint Word)>? Patches)? Predict(Entry previous, ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader, AddressRebase rebase, ulong[] probe) =>
        rebase.WithActive<(ResourceSnapshot, List<(int Buffer, uint Word)>?)?>(probe, () =>
        {
            if (!previous.Matches(plan, inputs, probe) || !rebase.AppliesTo(previous))
                return null;
            if (Validate(previous, residentReader, rebase, inputs.UserData, out _))
                return (rebase.Apply(WithCurrentUserData(previous.Snapshot, inputs.UserData), inputs.UserData), null);
            if (rebase.HasLearnableMemoryBases &&
                TryRebaseRefresh(previous, plan, inputs, residentReader, rebase, out var snapshot, out var patches))
                return (snapshot, patches);
            return null;
        });

    // Its own method: a lambda in MaterializeCore that captured the parameters made every call,
    // hits included, allocate the closure on entry.
    private void Learn(AddressRebase rebase, Entry built, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader) =>
        rebase.Learn(built, (previous, probe) => Predict(previous, plan, inputs, residentReader, rebase, probe));

    // The table for this draw without evaluating it: a slot that reads a memory-based buffer at a
    // constant offset takes the word now at that offset, which the moved window has just read; the
    // other slots read nothing that changed, so they keep the entry's words, except a base slot,
    // which the patch has already moved. current holds this draw's words in the entry's layout.
    // The table is patched in place: the caller passes a copy it owns.
    private static bool TryCopyTable(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, byte[] current, uint[] table)
    {
        var sources = entry.TableCopySources ??= TableCopySourcesOf(entry, plan, rebase, table.Length);
        if (sources.Length == 0)
            return false;
        for (var slot = 0; slot < sources.Length; slot++)
        {
            var offset = sources[slot];
            if (offset >= 0)
                table[(int)plan.TableReads[slot].FlatOffset] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
        }

        return true;
    }

    // A table whose slots are all direct loads or reads through memory-based buffers.
    private static bool CanCopyDirect(AddressRebase rebase)
    {
        if (rebase.DirectSlots is not null)
            return true;
        return false;
    }


    // Every slot takes the dword it loads for this draw. A direct slot's address is evaluated from
    // the draw's own user data, as the table evaluation would, and its dword is taken from the range
    // this draw read around it; a slot through a memory-based buffer copies as in TryCopyTable.
    // Nothing is written unless every slot is placed.
    private static bool TryCopyDirectTable(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, byte[] current,
        ulong[] moved, IReadOnlyList<uint> userData, uint[] table)
    {
        var through = entry.TableCopySources ??= TableCopySourcesOf(entry, plan, rebase, table.Length);
        var direct = rebase.DirectSlots!;
        if (through.Length != direct.Length)
            return Unplaced();
        Span<int> sources = direct.Length <= 256 ? stackalloc int[direct.Length] : new int[direct.Length];
        for (var slot = 0; slot < direct.Length; slot++)
        {
            if (plan.TableReads[slot].FlatOffset >= table.Length)
                return Unplaced();
            if (rebase.TableSlotBuffers[slot] >= 0)
            {
                if (through[slot] < 0)
                    return Unplaced();
                sources[slot] = through[slot];
                continue;
            }

            var load = direct[slot];
            if (load.Low >= userData.Count || load.High >= userData.Count)
                return Unplaced();
            var high = load.High >= 0 ? userData[load.High] : load.HighConstant;
            var baseAddress = ((high << 32) | userData[load.Low]) & 0x0000_FFFF_FFFF_FFFFul;
            if (RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, baseAddress, high, 0,
                    load.Immediate, load.Offset, out var address) != RuntimeValueEvaluator.RawAddress.Read ||
                !TryFindMovedWord(entry, moved, address, out sources[slot]))
                return Unplaced();
        }

        for (var slot = 0; slot < sources.Length; slot++)
            table[(int)plan.TableReads[slot].FlatOffset] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(sources[slot]));
        return true;

        static bool Unplaced()
        {
            return false;
        }
    }

    // Per table slot: the byte offset in the entry's words of the word the slot copies, or -1 for a
    // slot that keeps its word. Empty when some slot cannot be placed.
    private static int[] TableCopySourcesOf(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, int tableLength)
    {
        var sources = new int[plan.TableReads.Count];
        for (var slot = 0; slot < sources.Length; slot++)
        {
            sources[slot] = -1;
            var buffer = rebase.TableSlotBuffers[slot];
            if (buffer < 0)
                continue;
            var descriptor = entry.Snapshot.Buffers[buffer];
            if (!AddressRebase.TryMovedWindow(descriptor, descriptor[0], out var start, out var size) ||
                rebase.TableSlotOffsets[slot] < 0 || (ulong)rebase.TableSlotOffsets[slot] + sizeof(uint) > size ||
                !TryFindWord(entry, start + (ulong)rebase.TableSlotOffsets[slot], out var offset) ||
                plan.TableReads[slot].FlatOffset >= tableLength)
                return [];
            sources[slot] = offset;
        }

        return sources;
    }

    // Where each memory-loaded base word of the entry lies: its range and byte offset in it.
    private static (int Range, int Offset)[] BaseWordProbesOf(Entry entry)
    {
        var probes = new List<(int Range, int Offset)>();
        for (var range = 0; range < entry.RangeAddresses.Length; range++)
        {
            for (var offset = 0; offset < entry.RangeLengths[range]; offset += sizeof(uint))
            {
                if (entry.BaseWordBuffers![(entry.RangeOffsets[range] + offset) / sizeof(uint)] >= 0)
                    probes.Add((range, offset));
            }
        }

        return probes.ToArray();
    }

    private byte[] _rebaseCurrent = [];
    private ulong[] _rebaseMoved = [];
    private readonly List<(int Buffer, uint Word)> _rebaseBases = new();

    // Per recorded range: the memory-based buffer whose window wholly holds it, or NoBase. Such a
    // range is read only once the buffer's base is known: it moves with the base.
    private static int[] RangeWindowsOf(Entry entry, AddressRebase rebase)
    {
        var windows = new int[entry.RangeAddresses.Length];
        windows.AsSpan().Fill(NoBase);
        for (var index = 0; index < windows.Length; index++)
        {
            foreach (var buffer in rebase.MemoryBases)
            {
                var descriptor = entry.Snapshot.Buffers[buffer];
                if (descriptor.Length >= 3 && AddressRebase.TryMovedWindow(descriptor, descriptor[0], out var start, out var size) &&
                    entry.RangeAddresses[index] >= start && entry.RangeAddresses[index] - start + (ulong)entry.RangeLengths[index] <= size)
                {
                    windows[index] = buffer;
                    break;
                }
            }
        }

        return windows;
    }

    // A stale entry whose only changed words are memory-loaded buffer bases, and constants only the
    // table reads: the bases are patched into a copy of the snapshot and the table is evaluated
    // again for this draw, which reads the constants where the moved bases now point. Every read of
    // that evaluation is a word the entry validated unchanged, or lies in the window a patched base
    // now covers. The descriptors and specialization are the entry's; learning checks that.
    private bool TryRebaseRefresh(Entry entry, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, AddressRebase rebase, out ResourceSnapshot snapshot,
        out List<(int Buffer, uint Word)> patches)
    {
        snapshot = null!;
        patches = null!;
        if (entry.BaseWordBuffers is not { } map)
            return false;
        var userData = inputs.UserData;
        // The words read here are compared and copied, never kept, so the buffers are reused.
        if (_rebaseCurrent.Length < entry.Bytes.Length)
            _rebaseCurrent = new byte[Math.Max(entry.Bytes.Length, _rebaseCurrent.Length * 2)];
        if (_rebaseMoved.Length < entry.RangeAddresses.Length)
            _rebaseMoved = new ulong[Math.Max(entry.RangeAddresses.Length, _rebaseMoved.Length * 2)];
        var current = _rebaseCurrent;
        var moved = _rebaseMoved;
        var windowed = entry.RangeWindows ??= RangeWindowsOf(entry, rebase);
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (windowed[index] != NoBase)
                continue;
            moved[index] = rebase.MoveRead(entry, userData, entry.RangeAddresses[index], entry.RangeLengths[index]);
            if (!residentReader(moved[index], current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index]), entry.RangeClean[index]))
                return false;
        }

        // The new base of each buffer whose base word changed, from the ranges read so far. A base
        // word inside a moving window would need its own base first; such an entry is not rebased.
        var rangeCount = entry.RangeAddresses.Length;
        var bases = _rebaseBases;
        bases.Clear();
        foreach (var (range, rangeOffset) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
        {
            if (windowed[range] != NoBase)
                return false;
            var offset = entry.RangeOffsets[range] + rangeOffset;
            var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
            if (now != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset)))
                bases.Add((map[offset / sizeof(uint)], now));
        }

        for (var index = 0; index < rangeCount; index++)
        {
            var buffer = windowed[index];
            if (buffer == NoBase)
                continue;
            var oldBase = entry.Snapshot.Buffers[buffer][0];
            var newBase = oldBase;
            foreach (var (changed, word) in bases)
                if (changed == buffer)
                    newBase = word;
            moved[index] = entry.RangeAddresses[index] - oldBase + newBase;
            if (!residentReader(moved[index], current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index]), entry.RangeClean[index]))
                return false;
        }

        List<(int Buffer, uint Word)>? found = null;
        // A changed constant outside the moved windows is read by a slot the copy cannot place.
        var constantsOutsideWindows = false;
        for (var index = 0; index < rangeCount; index++)
        {
            // Most ranges did not change at all; only a changed one is looked at word by word.
            if (current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index])
                .SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index])))
                continue;
            for (var offset = entry.RangeOffsets[index]; offset < entry.RangeOffsets[index] + entry.RangeLengths[index]; offset += sizeof(uint))
            {
                var word = offset / sizeof(uint);
                var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
                if (now == System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset)))
                    continue;
                var buffer = map[word];
                if (buffer >= 0)
                {
                    if (rebase.IsRejectedMemoryBase(buffer))
                        return false;
                    (found ??= new()).Add((buffer, now));
                }
                else if (!entry.WordTableOnly[word])
                    return false;
                else
                    constantsOutsideWindows |= windowed[index] == NoBase;
            }
        }

        if (found is null)
            return false;
        var patched = rebase.PatchMemoryBases(rebase.Apply(WithCurrentUserData(entry.Snapshot, userData), userData), found);
        // A changed constant outside the windows is still copied when every slot is a direct load:
        // each slot's word is then the dword at its own address, which this draw has just read.
        var copyDirect = constantsOutsideWindows && CanCopyDirect(rebase);
        if (plan.TableReads.Count != 0 && (!constantsOutsideWindows || copyDirect) && !rebase.TableSlotsThroughBases)
        {
            // The patch cloned the table when it moved a base slot; otherwise it is the entry's.
            var table = ReferenceEquals(patched.FlattenedResourceTable, entry.Snapshot.FlattenedResourceTable)
                ? (uint[])patched.FlattenedResourceTable.Clone()
                : patched.FlattenedResourceTable;
            // The copy writes nothing unless every slot can be placed.
            if (copyDirect
                    ? TryCopyDirectTable(entry, plan, rebase, current, moved, userData, table)
                    : TryCopyTable(entry, plan, rebase, current, table))
            {
                snapshot = new ResourceSnapshot
                {
                    Buffers = patched.Buffers,
                    Images = patched.Images,
                    Samplers = patched.Samplers,
                    FlattenedResourceTable = table,
                    UserData = patched.UserData,
                    DeviceAddressRanges = patched.DeviceAddressRanges,
                };
                patches = found;
                return true;
            }
        }

        if (plan.TableReads.Count != 0)
        {
            if (!entry.TableRefreshable)
                return false;
            var windows = new List<(ulong Start, ulong Size)>(found.Count);
            foreach (var (buffer, baseWord) in found)
                if (AddressRebase.TryMovedWindow(patched.Buffers[buffer], baseWord, out var start, out var size))
                    windows.Add((start, size));

            var recorder = TakeRecorder();
            try
            {
                var recording = new ResourceRuntimeInputs
                {
                    UserData = userData,
                    ShaderBase = inputs.ShaderBase,
                    ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                    ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                    ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                    ReadsClean = inputs.ReadsClean,
                    ComputeState = inputs.ComputeState,
                };
                var cachedTable = entry.Snapshot.FlattenedResourceTable;
                ReadingTable = true;
                bool evaluated;
                uint[] table;
                try
                {
                    evaluated = ResourceMaterializer.TryEvaluateTable(plan, recording, out table);
                }
                finally
                {
                    ReadingTable = false;
                }

                if (!evaluated || recorder.Failed || table.Length != cachedTable.Length)
                    return false;

                foreach (var (address, word, _, _) in recorder.Reads)
                {
                    var inWindow = false;
                    foreach (var (start, size) in windows)
                        inWindow |= address >= start && address - start < size;
                    if (inWindow)
                        continue;
                    if (!TryFindMovedWord(entry, moved, address, out var offset) ||
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
                        return false;
                }

                // The written device-address slots come from reads validated unchanged above.
                foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
                    cachedTable.AsSpan((int)slot, ShaderResourcePlan.WrittenRangeDwordCount).CopyTo(table.AsSpan((int)slot));
                patched = new ResourceSnapshot
                {
                    Buffers = patched.Buffers,
                    Images = patched.Images,
                    Samplers = patched.Samplers,
                    FlattenedResourceTable = table,
                    UserData = patched.UserData,
                    DeviceAddressRanges = patched.DeviceAddressRanges,
                };
            }
            finally
            {
                ReturnRecorder(recorder);
            }
        }

        snapshot = patched;
        patches = found;
        return true;
    }

    private bool Validate(Entry entry, ResidentGuestBytesReader residentReader, AddressRebase rebase, IReadOnlyList<uint> userData,
        out bool unreadable)
    {
        unreadable = false;
        // A memory-loaded base usually moved when anything did: its few bytes are checked before
        // ranges that can span kilobytes. The full comparison below still decides a match.
        if (entry.BaseWordBuffers is not null)
        {
            Span<byte> word = stackalloc byte[sizeof(uint)];
            foreach (var (range, offset) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
            {
                var address = rebase.MoveRead(entry, userData, entry.RangeAddresses[range], entry.RangeLengths[range]) + (ulong)offset;
                if (residentReader(address, word, entry.RangeClean[range]) &&
                    !word.SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[range] + offset, sizeof(uint))))
                    return false;
            }
        }

        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            var length = entry.RangeLengths[index];
            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            var address = rebase.MoveRead(entry, userData, entry.RangeAddresses[index], length);
            if (!residentReader(address, current, entry.RangeClean[index]))
            {
                unreadable = true;
                return false;
            }

            if (!current.SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[index], length)))
                return false;
        }

        return true;
    }

    private sealed class Entry
    {
        public required ShaderResourcePlan Plan { get; init; }
        public required uint[] UserData { get; init; }
        // The user data words the evaluator read, one bit per word.
        public required ulong[] UsedUserData { get; init; }
        public required ulong ShaderBase { get; init; }
        public required ComputeSelectorState? ComputeState { get; init; }
        public required ulong[] RangeAddresses { get; init; }
        public required int[] RangeOffsets { get; init; }
        public required int[] RangeLengths { get; init; }
        public required bool[] RangeClean { get; init; }
        // Per recorded dword: read only while the flattened table was evaluated.
        public required bool[] WordTableOnly { get; init; }
        // The table has the plan's plain layout, so no specialization read or extended it.
        public required bool TableRefreshable { get; init; }
        public required byte[] Bytes { get; init; }
        public required ResourceSnapshot Snapshot { get; init; }
        public required ResourceSpecialization Specialization { get; init; }
        // Per recorded dword: the buffer whose memory-loaded base dword it is, or a negative value.
        public int[]? BaseWordBuffers { get; init; }
        // Filled on the first rebased refresh of the entry; see RangeWindowsOf and TableCopySourcesOf.
        public int[]? RangeWindows { get; set; }
        public int[]? TableCopySources { get; set; }
        public (int Range, int Offset)[]? BaseWordProbes { get; set; }
        public Entry? Next { get; set; }

        // A rebased word is an address the hit patches in, so it does not distinguish entries.
        public bool Matches(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased)
        {
            if (!ReferenceEquals(Plan, plan) || ShaderBase != inputs.ShaderBase ||
                !Nullable.Equals(ComputeState, inputs.ComputeState) || UserData.Length != inputs.UserData.Count)
                return false;
            for (var index = 0; index < UserData.Length; index++)
                if (IsUsed(UsedUserData, index) && !IsUsed(rebased, index) && UserData[index] != inputs.UserData[index])
                    return false;
            return true;
        }
    }

    // A user data list that notes which words are read by index. Copying it out (the snapshot's
    // own copy) goes through the enumerator and is not a read of the evaluator.
    private sealed class UserDataRecorder : IReadOnlyList<uint>
    {
        private IReadOnlyList<uint> _inner = [];
        private ulong[] _used = [];

        public void Begin(IReadOnlyList<uint> inner)
        {
            _inner = inner;
            _used = new ulong[(inner.Count + 63) >> 6];
        }

        // The recorded set; the next Begin allocates a fresh one, so the entry keeps this array.
        public ulong[] TakeUsed() => _used;

        public int Count => _inner.Count;

        public uint this[int index]
        {
            get
            {
                _used[index >> 6] |= 1UL << (index & 63);
                return _inner[index];
            }
        }

        public IEnumerator<uint> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReadRecorder
    {
        private readonly List<(ulong Address, uint Word, bool Clean, bool Table)> _reads = new();
        private bool _inTable;
        private GuestWordReader? _reader;
        private GuestWordReader? _cleanReader;
        private ResidentGuestBytesReader? _residentReader;
        private readonly GuestWordReader _recordRead;
        private readonly GuestWordReader _recordCleanRead;
        private readonly ResidentGuestBytesReader _recordResidentRead;

        public ReadRecorder()
        {
            _recordRead = (ulong address, out uint word) => Read(_reader!, address, out word, clean: false);
            _recordCleanRead = (ulong address, out uint word) => Read(_cleanReader!, address, out word, clean: true);
            _recordResidentRead = (ulong address, Span<byte> destination, bool clean) => ReadResident(address, destination, clean);
            TablePhase = inTable =>
            {
                _inTable = inTable;
                ReadingTable = inTable;
            };
        }

        public void Reset()
        {
            _reads.Clear();
            // Retain ordinary descriptor walks without keeping unusually large
            // tables alive for the lifetime of the renderer.
            if (_reads.Capacity > 16384)
                _reads.Capacity = 0;
            _reader = null;
            _cleanReader = null;
            _residentReader = null;
            _inTable = false;
            Failed = false;
        }

        public bool Failed { get; private set; }

        private readonly UserDataRecorder _userData = new();

        public IReadOnlyList<uint> WrapUserData(IReadOnlyList<uint> inner)
        {
            _userData.Begin(inner);
            return _userData;
        }

        public List<(ulong Address, uint Word, bool Clean, bool Table)> Reads => _reads;

        public Action<bool> TablePhase { get; }

        public GuestWordReader? Wrap(GuestWordReader? inner, bool clean)
        {
            if (inner is null)
                return null;
            if (clean)
            {
                _cleanReader = inner;
                return _recordCleanRead;
            }
            _reader = inner;
            return _recordRead;
        }

        private bool Read(GuestWordReader inner, ulong address, out uint word, bool clean)
        {
            if (!inner(address, out word))
            {
                Failed = true;
                return false;
            }
            _reads.Add((address, word, clean, _inTable));
            return true;
        }

        public ResidentGuestBytesReader? WrapResident(ResidentGuestBytesReader? inner)
        {
            if (inner is null)
                return null;
            _residentReader = inner;
            return _recordResidentRead;
        }

        private bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (!_residentReader!(address, destination, clean))
                return false;

            for (var offset = 0; offset + sizeof(uint) <= destination.Length; offset += sizeof(uint))
                _reads.Add((address + (ulong)offset,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[offset..]), clean, _inTable));
            return true;
        }

        public Entry Build(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization, IReadOnlyList<int> memoryBases)
        {
            // Reads mostly arrive in address order; sorting a sorted list was ~5% of a draw-heavy render thread.
            var ordered = true;
            for (var index = 1; ordered && index < _reads.Count; index++)
                ordered = _reads[index - 1].Address <= _reads[index].Address;
            if (!ordered)
                _reads.Sort((left, right) => left.Address.CompareTo(right.Address));
            // Count unique words and contiguous ranges first, then fill the
            // retained arrays directly. Temporary lists previously duplicated
            // every recorded byte and range on each cache miss.
            var rangeCount = 0;
            var wordCount = 0;
            ulong end = 0;
            var previous = ulong.MaxValue;
            foreach (var read in _reads)
            {
                if (read.Address == previous) continue;
                if (wordCount == 0 || read.Address != end) rangeCount++;
                wordCount++;
                previous = read.Address;
                end = read.Address + sizeof(uint);
            }

            var addresses = new ulong[rangeCount];
            var offsets = new int[rangeCount];
            var lengths = new int[rangeCount];
            var clean = new bool[rangeCount];
            var bytes = new byte[wordCount * sizeof(uint)];
            var tableOnly = new bool[wordCount];
            var rangeIndex = -1;
            var wordIndex = -1;
            end = 0;
            previous = ulong.MaxValue;
            foreach (var (address, word, wordClean, table) in _reads)
            {
                if (address == previous)
                {
                    clean[rangeIndex] |= wordClean;
                    tableOnly[wordIndex] &= table;
                    continue;
                }

                wordIndex++;
                if (rangeIndex < 0 || address != end)
                {
                    rangeIndex++;
                    addresses[rangeIndex] = address;
                    offsets[rangeIndex] = wordIndex * sizeof(uint);
                }
                lengths[rangeIndex] += sizeof(uint);
                clean[rangeIndex] |= wordClean;
                tableOnly[wordIndex] = table;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(wordIndex * sizeof(uint)), word);
                previous = address;
                end = address + sizeof(uint);
            }

            var userData = new uint[inputs.UserData.Count];
            for (var index = 0; index < userData.Length; index++)
                userData[index] = inputs.UserData[index];
            return new Entry
            {
                BaseWordBuffers = memoryBases.Count == 0 ? null : MapBaseWords(snapshot, memoryBases, bytes),
                Plan = plan,
                UserData = userData,
                UsedUserData = _userData.TakeUsed(),
                ShaderBase = inputs.ShaderBase,
                ComputeState = inputs.ComputeState,
                RangeAddresses = addresses,
                RangeOffsets = offsets,
                RangeLengths = lengths,
                RangeClean = clean,
                WordTableOnly = tableOnly,
                TableRefreshable = snapshot.FlattenedResourceTable.Length ==
                    plan.TableReads.Count + plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount,
                Bytes = bytes,
                Snapshot = snapshot,
                Specialization = specialization,
            };
        }

        // The recorded word each memory-loaded base dword came from: the only recorded word holding
        // that value. A word two bases claim names neither. Learning checks the guess.
        private static int[]? MapBaseWords(ResourceSnapshot snapshot, IReadOnlyList<int> memoryBases, byte[] bytes)
        {
            int[]? map = null;
            var wordCount = bytes.Length / sizeof(uint);
            foreach (var buffer in memoryBases)
            {
                if (buffer >= snapshot.Buffers.Length || snapshot.Buffers[buffer].Length < 2)
                    continue;
                var value = snapshot.Buffers[buffer][0];
                var match = -1;
                for (var word = 0; word < wordCount; word++)
                {
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(word * sizeof(uint))) != value)
                        continue;
                    if (match >= 0)
                    {
                        match = -1;
                        break;
                    }
                    match = word;
                }

                if (match < 0)
                    continue;
                if (map is null)
                {
                    map = new int[wordCount];
                    map.AsSpan().Fill(NoBase);
                }

                map[match] = map[match] == NoBase ? buffer : SharedBase;
            }

            return map;
        }
    }

    private const int NoBase = -1;
    private const int SharedBase = -2;
}
