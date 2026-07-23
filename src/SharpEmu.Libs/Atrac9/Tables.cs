#nullable disable
using System;
using static LibAtrac9.HuffmanCodebooks;

namespace LibAtrac9
{
    internal static class Tables
    {
        public static int MaxHuffPrecision(bool highSampleRate) => highSampleRate ? 1 : 7;
        public static int MinBandCount(bool highSampleRate) => highSampleRate ? 1 : 3;
        public static int MaxExtensionBand(bool highSampleRate) => highSampleRate ? 16 : 18;

        public static readonly int[] SampleRates =
        {
            11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
            44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000
        };

        public static readonly byte[] SamplingRateIndexToFrameSamplesPower = { 6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8 };

        // From sampling rate index
        public static readonly byte[] MaxBandCount = { 8, 8, 12, 12, 12, 18, 18, 18, 8, 8, 12, 12, 12, 16, 16, 16 };
        public static readonly byte[] BandToQuantUnitCount = { 0, 4, 8, 10, 12, 13, 14, 15, 16, 18, 20, 21, 22, 23, 24, 25, 26, 28, 30 };

        public static readonly byte[] QuantUnitToCoeffCount =
        {
            02, 02, 02, 02, 02, 02, 02, 02, 04, 04, 04, 04, 08, 08, 08,
            08, 08, 08, 08, 08, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16
        };

        public static readonly short[] QuantUnitToCoeffIndex =
        {
            0, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56,
            64, 72, 80, 88, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 256
        };

        public static readonly byte[] QuantUnitToCodebookIndex =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2,
            2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
        };

        public static readonly ChannelConfig[] ChannelConfig =
        {
            new ChannelConfig(BlockType.Mono),
            new ChannelConfig(BlockType.Mono, BlockType.Mono),
            new ChannelConfig(BlockType.Stereo),
            new ChannelConfig(BlockType.Stereo, BlockType.Mono, BlockType.LFE, BlockType.Stereo),
            new ChannelConfig(BlockType.Stereo, BlockType.Mono, BlockType.LFE, BlockType.Stereo, BlockType.Stereo),
            new ChannelConfig(BlockType.Stereo, BlockType.Stereo)
        };

        public static readonly HuffmanCodebook[] HuffmanScaleFactorsUnsigned =
            GenerateHuffmanCodebooks(HuffmanScaleFactorsACodes, HuffmanScaleFactorsABits, HuffmanScaleFactorsGroupSizes);
        
        public static readonly HuffmanCodebook[] HuffmanScaleFactorsSigned =
            GenerateHuffmanCodebooks(HuffmanScaleFactorsBCodes, HuffmanScaleFactorsBBits, HuffmanScaleFactorsGroupSizes);

        public static readonly HuffmanCodebook[][][] HuffmanSpectrum =
        {
            GenerateHuffmanCodebooks(HuffmanSpectrumACodes, HuffmanSpectrumABits, HuffmanSpectrumAGroupSizes),
            GenerateHuffmanCodebooks(HuffmanSpectrumBCodes, HuffmanSpectrumBBits, HuffmanSpectrumBGroupSizes)
        };

        public static readonly double[][] ImdctWindow = { GenerateImdctWindow(6), GenerateImdctWindow(7), GenerateImdctWindow(8) };

        public static readonly double[] SpectrumScale = Generate(32, SpectrumScaleFunction);
        public static readonly double[] QuantizerStepSize = Generate(16, QuantizerStepSizeFunction);
        public static readonly double[] QuantizerFineStepSize = Generate(16, QuantizerFineStepSizeFunction);

        public static readonly byte[][] GradientCurves = BitAllocation.GenerateGradientCurves();

        private static double QuantizerStepSizeFunction(int x) => 2.0 / ((1 << (x + 1)) - 1);
        private static double QuantizerFineStepSizeFunction(int x) => QuantizerStepSizeFunction(x) / ushort.MaxValue;
        private static double SpectrumScaleFunction(int x) => Math.Pow(2, x - 15);

        private static double[] GenerateImdctWindow(int frameSizePower)
        {
            int frameSize = 1 << frameSizePower;
            var output = new double[frameSize];

            double[] a1 = GenerateMdctWindow(frameSizePower);

            for (int i = 0; i < frameSize; i++)
            {
                output[i] = a1[i] / (a1[frameSize - 1 - i] * a1[frameSize - 1 - i] + a1[i] * a1[i]);
            }
            return output;
        }

        private static double[] GenerateMdctWindow(int frameSizePower)
        {
            int frameSize = 1 << frameSizePower;
            var output = new double[frameSize];

            for (int i = 0; i < frameSize; i++)
            {
                output[i] = (Math.Sin(((i + 0.5) / frameSize - 0.5) * Math.PI) + 1.0) * 0.5;
            }

            return output;
        }

        private static T[] Generate<T>(int count, Func<int, T> elementGenerator)
        {
            var table = new T[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = elementGenerator(i);
            }
            return table;
        }

        
    }
}
