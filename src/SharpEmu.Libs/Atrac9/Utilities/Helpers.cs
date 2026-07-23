#nullable disable
using System.Runtime.CompilerServices;

namespace LibAtrac9.Utilities
{
    internal static class Helpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short Clamp16(int value)
        {
            if (value > short.MaxValue)
                return short.MaxValue;
            if (value < short.MinValue)
                return short.MinValue;
            return (short)value;
        }

        public static int GetNextMultiple(int value, int multiple)
        {
            if (multiple <= 0)
                return value;

            if (value % multiple == 0)
                return value;

            return value + multiple - value % multiple;
        }

        /// <summary>
        /// Returns the floor of the base 2 logarithm of a specified number.
        /// </summary>
        /// <param name="value">The number whose logarithm is to be found.</param>
        /// <returns>The floor of the base 2 logarithm of <paramref name="value"/>.</returns>
        public static int Log2(int value)
        {
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;

            return MultiplyDeBruijnBitPosition[(uint)(value * 0x07C4ACDDU) >> 27];
        }

        private static readonly int[] MultiplyDeBruijnBitPosition =
        {
            0, 9, 1, 10, 13, 21, 2, 29, 11, 14, 16, 18, 22, 25, 3, 30,
            8, 12, 20, 28, 15, 17, 24, 7, 19, 27, 23, 6, 26, 5, 4, 31
        };
    }
}
