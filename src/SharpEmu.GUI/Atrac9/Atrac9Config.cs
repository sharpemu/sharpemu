#nullable disable
using System.IO;
using LibAtrac9.Utilities;

namespace LibAtrac9
{
    /// <summary>
    /// Stores the configuration data needed to decode or encode an ATRAC9 stream.
    /// </summary>
    public class Atrac9Config
    {
        /// <summary>
        /// The 4-byte ATRAC9 configuration data.
        /// </summary>
        public byte[] ConfigData { get; }

        /// <summary>
        /// A 4-bit value specifying one of 16 sample rates.
        /// </summary>
        public int SampleRateIndex { get; }
        /// <summary>
        /// A 3-bit value specifying one of 6 substream channel mappings.
        /// </summary>
        public int ChannelConfigIndex { get; }
        /// <summary>
        /// An 11-bit value containing the average size of a single frame.
        /// </summary>
        public int FrameBytes { get; }
        /// <summary>
        /// A 2-bit value indicating how many frames are in each superframe.
        /// </summary>
        public int SuperframeIndex { get; }

        /// <summary>
        /// The channel mapping used by the ATRAC9 stream.
        /// </summary>
        public ChannelConfig ChannelConfig { get; }
        /// <summary>
        /// The total number of channels in the ATRAC9 stream.
        /// </summary>
        public int ChannelCount { get; }
        /// <summary>
        /// The sample rate of the ATRAC9 stream.
        /// </summary>
        public int SampleRate { get; }
        /// <summary>
        /// Indicates whether the ATRAC9 stream has a <see cref="SampleRateIndex"/> of 8 or above.
        /// </summary>
        public bool HighSampleRate { get; }

        /// <summary>
        /// The number of frames in each superframe.
        /// </summary>
        public int FramesPerSuperframe { get; }
        /// <summary>
        /// The number of samples in one frame as an exponent of 2.
        /// <see cref="FrameSamples"/> = 2^<see cref="FrameSamplesPower"/>.
        /// </summary>
        public int FrameSamplesPower { get; }
        /// <summary>
        /// The number of samples in one frame.
        /// </summary>
        public int FrameSamples { get; }
        /// <summary>
        /// The number of bytes in one superframe.
        /// </summary>
        public int SuperframeBytes { get; }
        /// <summary>
        /// The number of samples in one superframe.
        /// </summary>
        public int SuperframeSamples { get; }

        /// <summary>
        /// Reads ATRAC9 configuration data and calculates the stream parameters from it.
        /// </summary>
        /// <param name="configData">The processed ATRAC9 configuration.</param>
        public Atrac9Config(byte[] configData)
        {
            if (configData == null || configData.Length != 4)
            {
                throw new InvalidDataException("Config data must be 4 bytes long");
            }

            ReadConfigData(configData, out int a, out int b, out int c, out int d);
            SampleRateIndex = a;
            ChannelConfigIndex = b;
            FrameBytes = c;
            SuperframeIndex = d;
            ConfigData = configData;

            FramesPerSuperframe = 1 << SuperframeIndex;
            SuperframeBytes = FrameBytes << SuperframeIndex;
            ChannelConfig = Tables.ChannelConfig[ChannelConfigIndex];

            ChannelCount = ChannelConfig.ChannelCount;
            SampleRate = Tables.SampleRates[SampleRateIndex];
            HighSampleRate = SampleRateIndex > 7;
            FrameSamplesPower = Tables.SamplingRateIndexToFrameSamplesPower[SampleRateIndex];
            FrameSamples = 1 << FrameSamplesPower;
            SuperframeSamples = FrameSamples * FramesPerSuperframe;
        }

        private static void ReadConfigData(byte[] configData, out int sampleRateIndex, out int channelConfigIndex, out int frameBytes, out int superframeIndex)
        {
            var reader = new BitReader(configData);

            int header = reader.ReadInt(8);
            sampleRateIndex = reader.ReadInt(4);
            channelConfigIndex = reader.ReadInt(3);
            int validationBit = reader.ReadInt(1);
            frameBytes = reader.ReadInt(11) + 1;
            superframeIndex = reader.ReadInt(2);

            if (header != 0xFE || validationBit != 0)
            {
                throw new InvalidDataException("ATRAC9 Config Data is invalid");
            }
        }
    }
}
