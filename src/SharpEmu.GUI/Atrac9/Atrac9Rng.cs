#nullable disable
namespace LibAtrac9
{
    /// <summary>
    /// An Xorshift RNG used by the ATRAC9 codec
    /// </summary>
    internal class Atrac9Rng
    {
        private ushort _stateA;
        private ushort _stateB;
        private ushort _stateC;
        private ushort _stateD;

        public Atrac9Rng(ushort seed)
        {
            int startValue = 0x4D93 * (seed ^ (seed >> 14));

            _stateA = (ushort)(3 - startValue);
            _stateB = (ushort)(2 - startValue);
            _stateC = (ushort)(1 - startValue);
            _stateD = (ushort)(0 - startValue);
        }

        public ushort Next()
        {
            ushort t = (ushort)(_stateD ^ (_stateD << 5));
            _stateD = _stateC;
            _stateC = _stateB;
            _stateB = _stateA;
            _stateA = (ushort)(t ^ _stateA ^ ((t ^ (_stateA >> 5)) >> 4));
            return _stateA;
        }
    }
}
