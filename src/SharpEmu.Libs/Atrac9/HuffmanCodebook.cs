#nullable disable
using System;
using LibAtrac9.Utilities;

namespace LibAtrac9
{
    internal class HuffmanCodebook
    {
        public HuffmanCodebook(short[] codes, byte[] bits, byte valueCountPower)
        {
            Codes = codes;
            Bits = bits;
            if (Codes == null || Bits == null) return;

            ValueCount = 1 << valueCountPower;
            ValueCountPower = valueCountPower;
            ValueBits = Helpers.Log2(codes.Length) >> valueCountPower;
            ValueMax = 1 << ValueBits;

            int max = 0;
            foreach (byte bitSize in bits)
            {
                max = Math.Max(max, bitSize);
            }

            MaxBitSize = max;
            Lookup = CreateLookupTable();
        }

        private byte[] CreateLookupTable()
        {
            if (Codes == null || Bits == null) return null;

            int tableSize = 1 << MaxBitSize;
            var dest = new byte[tableSize];

            for (int i = 0; i < Bits.Length; i++)
            {
                if (Bits[i] == 0) continue;
                int unusedBits = MaxBitSize - Bits[i];

                int start = Codes[i] << unusedBits;
                int length = 1 << unusedBits;
                int end = start + length;

                for (int j = start; j < end; j++)
                {
                    dest[j] = (byte)i;
                }
            }
            return dest;
        }

        public short[] Codes { get; }
        public byte[] Bits { get; }
        public byte[] Lookup { get; }
        public int ValueCount { get; }
        public int ValueCountPower { get; }
        public int ValueBits { get; }
        public int ValueMax { get; }
        public int MaxBitSize { get; }
    }
}
