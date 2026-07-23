#nullable disable
using System;
using System.IO;
using LibAtrac9.Utilities;

namespace LibAtrac9
{
    internal static class Unpack
    {
        public static void UnpackFrame(BitReader reader, Frame frame)
        {
            foreach (Block block in frame.Blocks)
            {
                UnpackBlock(reader, block);
            }
        }

        private static void UnpackBlock(BitReader reader, Block block)
        {
            ReadBlockHeader(reader, block);

            if (block.BlockType == BlockType.LFE)
            {
                UnpackLfeBlock(reader, block);
            }
            else
            {
                UnpackStandardBlock(reader, block);
            }

            reader.AlignPosition(8);
        }

        private static void ReadBlockHeader(BitReader reader, Block block)
        {
            bool firstInSuperframe = block.Frame.FrameIndex == 0;
            block.FirstInSuperframe = !reader.ReadBool();
            block.ReuseBandParams = reader.ReadBool();

            if (block.FirstInSuperframe != firstInSuperframe)
            {
                throw new InvalidDataException();
            }

            if (firstInSuperframe && block.ReuseBandParams && block.BlockType != BlockType.LFE)
            {
                throw new InvalidDataException();
            }
        }

        private static void UnpackStandardBlock(BitReader reader, Block block)
        {
            Channel[] channels = block.Channels;

            if (!block.ReuseBandParams)
            {
                ReadBandParams(reader, block);
            }

            ReadGradientParams(reader, block);
            BitAllocation.CreateGradient(block);
            ReadStereoParams(reader, block);
            ReadExtensionParams(reader, block);

            foreach (Channel channel in channels)
            {
                channel.UpdateCodedUnits();

                ScaleFactors.Read(reader, channel);
                BitAllocation.CalculateMask(channel);
                BitAllocation.CalculatePrecisions(channel);
                CalculateSpectrumCodebookIndex(channel);

                ReadSpectra(reader, channel);
                ReadSpectraFine(reader, channel);
            }

            block.QuantizationUnitsPrev = block.BandExtensionEnabled ? block.ExtensionUnit : block.QuantizationUnitCount;
        }

        private static void ReadBandParams(BitReader reader, Block block)
        {
            int minBandCount = Tables.MinBandCount(block.Config.HighSampleRate);
            int maxExtensionBand = Tables.MaxExtensionBand(block.Config.HighSampleRate);
            block.BandCount = reader.ReadInt(4);
            block.BandCount += minBandCount;
            block.QuantizationUnitCount = Tables.BandToQuantUnitCount[block.BandCount];
            if (block.BandCount < minBandCount || block.BandCount >
                Tables.MaxBandCount[block.Config.SampleRateIndex])
            {
                return;
            }

            if (block.BlockType == BlockType.Stereo)
            {
                block.StereoBand = reader.ReadInt(4);
                block.StereoBand += minBandCount;
                block.StereoQuantizationUnit = Tables.BandToQuantUnitCount[block.StereoBand];
            }
            else
            {
                block.StereoBand = block.BandCount;
            }

            block.BandExtensionEnabled = reader.ReadBool();
            if (block.BandExtensionEnabled)
            {
                block.ExtensionBand = reader.ReadInt(4);
                block.ExtensionBand += minBandCount;

                if (block.ExtensionBand < block.BandCount || block.ExtensionBand > maxExtensionBand)
                {
                    throw new InvalidDataException();
                }

                block.ExtensionUnit = Tables.BandToQuantUnitCount[block.ExtensionBand];
            }
            else
            {
                block.ExtensionBand = block.BandCount;
                block.ExtensionUnit = block.QuantizationUnitCount;
            }
        }

        private static void ReadGradientParams(BitReader reader, Block block)
        {
            block.GradientMode = reader.ReadInt(2);
            if (block.GradientMode > 0)
            {
                block.GradientEndUnit = 31;
                block.GradientEndValue = 31;
                block.GradientStartUnit = reader.ReadInt(5);
                block.GradientStartValue = reader.ReadInt(5);
            }
            else
            {
                block.GradientStartUnit = reader.ReadInt(6);
                block.GradientEndUnit = reader.ReadInt(6) + 1;
                block.GradientStartValue = reader.ReadInt(5);
                block.GradientEndValue = reader.ReadInt(5);
            }
            block.GradientBoundary = reader.ReadInt(4);

            if (block.GradientBoundary > block.QuantizationUnitCount)
            {
                throw new InvalidDataException();
            }
            if (block.GradientStartUnit < 1 || block.GradientStartUnit >= 48)
            {
                throw new InvalidDataException();
            }
            if (block.GradientEndUnit < 1 || block.GradientEndUnit >= 48)
            {
                throw new InvalidDataException();
            }
            if (block.GradientStartUnit > block.GradientEndUnit)
            {
                throw new InvalidDataException();
            }
            if (block.GradientStartValue < 0 || block.GradientStartValue >= 32)
            {
                throw new InvalidDataException();
            }
            if (block.GradientEndValue < 0 || block.GradientEndValue >= 32)
            {
                throw new InvalidDataException();
            }
        }

        private static void ReadStereoParams(BitReader reader, Block block)
        {
            if (block.BlockType != BlockType.Stereo) return;

            block.PrimaryChannelIndex = reader.ReadInt(1);
            block.HasJointStereoSigns = reader.ReadBool();
            if (block.HasJointStereoSigns)
            {
                for (int i = block.StereoQuantizationUnit; i < block.QuantizationUnitCount; i++)
                {
                    block.JointStereoSigns[i] = reader.ReadInt(1);
                }
            }
            else
            {
                Array.Clear(block.JointStereoSigns, 0, block.JointStereoSigns.Length);
            }
        }

        private static void ReadExtensionParams(BitReader reader, Block block)
        {
            // ReSharper disable once RedundantAssignment
            int bexBand = 0;
            if (block.BandExtensionEnabled)
            {
                BandExtension.GetBexBandInfo(out bexBand, out _, out _, block.QuantizationUnitCount);
                if (block.BlockType == BlockType.Stereo)
                {
                    ReadHeader(block.Channels[1]);
                }
                else
                {
                    reader.Position += 1;
                }
            }
            block.HasExtensionData = reader.ReadBool();

            if (!block.HasExtensionData) return;
            if (!block.BandExtensionEnabled)
            {
                block.BexMode = reader.ReadInt(2);
                block.BexDataLength = reader.ReadInt(5);
                reader.Position += block.BexDataLength;
                return;
            }

            ReadHeader(block.Channels[0]);

            block.BexDataLength = reader.ReadInt(5);
            if (block.BexDataLength <= 0) return;
            int bexDataEnd = reader.Position + block.BexDataLength;

            ReadData(block.Channels[0]);

            if (block.BlockType == BlockType.Stereo)
            {
                ReadData(block.Channels[1]);
            }

            // Make sure we didn't read too many bits
            if (reader.Position > bexDataEnd)
            {
                throw new InvalidDataException();
            }

            void ReadHeader(Channel channel)
            {
                int bexMode = reader.ReadInt(2);
                channel.BexMode = bexBand > 2 ? bexMode : 4;
                channel.BexValueCount = BandExtension.BexEncodedValueCounts[channel.BexMode][bexBand];
            }

            void ReadData(Channel channel)
            {
                for (int i = 0; i < channel.BexValueCount; i++)
                {
                    int dataLength = BandExtension.BexDataLengths[channel.BexMode][bexBand][i];
                    channel.BexValues[i] = reader.ReadInt(dataLength);
                }
            }
        }

        private static void CalculateSpectrumCodebookIndex(Channel channel)
        {
            Array.Clear(channel.CodebookSet, 0, channel.CodebookSet.Length);
            int quantUnits = channel.CodedQuantUnits;
            int[] sf = channel.ScaleFactors;

            if (quantUnits <= 1) return;
            if (channel.Config.HighSampleRate) return;

            // Temporarily setting this value allows for simpler code by
            // making the last value a non-special case.
            int originalScaleTmp = sf[quantUnits];
            sf[quantUnits] = sf[quantUnits - 1];

            int avg = 0;
            if (quantUnits > 12)
            {
                for (int i = 0; i < 12; i++)
                {
                    avg += sf[i];
                }
                avg = (avg + 6) / 12;
            }

            for (int i = 8; i < quantUnits; i++)
            {
                int prevSf = sf[i - 1];
                int nextSf = sf[i + 1];
                int minSf = Math.Min(prevSf, nextSf);
                if (sf[i] - minSf >= 3 || sf[i] - prevSf + sf[i] - nextSf >= 3)
                {
                    channel.CodebookSet[i] = 1;
                }
            }

            for (int i = 12; i < quantUnits; i++)
            {
                if (channel.CodebookSet[i] == 0)
                {
                    int minSf = Math.Min(sf[i - 1], sf[i + 1]);
                    if (sf[i] - minSf >= 2 && sf[i] >= avg - (Tables.QuantUnitToCoeffCount[i] == 16 ? 1 : 0))
                    {
                        channel.CodebookSet[i] = 1;
                    }
                }
            }

            sf[quantUnits] = originalScaleTmp;
        }

        private static void ReadSpectra(BitReader reader, Channel channel)
        {
            int[] values = channel.SpectraValuesBuffer;
            Array.Clear(channel.QuantizedSpectra, 0, channel.QuantizedSpectra.Length);
            int maxHuffPrecision = Tables.MaxHuffPrecision(channel.Config.HighSampleRate);

            for (int i = 0; i < channel.CodedQuantUnits; i++)
            {
                int subbandCount = Tables.QuantUnitToCoeffCount[i];
                int precision = channel.Precisions[i] + 1;
                if (precision <= maxHuffPrecision)
                {
                    HuffmanCodebook huff = Tables.HuffmanSpectrum[channel.CodebookSet[i]][precision][Tables.QuantUnitToCodebookIndex[i]];
                    int groupCount = subbandCount >> huff.ValueCountPower;
                    for (int j = 0; j < groupCount; j++)
                    {
                        values[j] = ReadHuffmanValue(huff, reader);
                    }

                    DecodeHuffmanValues(channel.QuantizedSpectra, Tables.QuantUnitToCoeffIndex[i], subbandCount, huff, values);
                }
                else
                {
                    int subbandIndex = Tables.QuantUnitToCoeffIndex[i];
                    for (int j = subbandIndex; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
                    {
                        channel.QuantizedSpectra[j] = reader.ReadSignedInt(precision);
                    }
                }
            }
        }

        private static void ReadSpectraFine(BitReader reader, Channel channel)
        {
            Array.Clear(channel.QuantizedSpectraFine, 0, channel.QuantizedSpectraFine.Length);

            for (int i = 0; i < channel.CodedQuantUnits; i++)
            {
                if (channel.PrecisionsFine[i] > 0)
                {
                    int overflowBits = channel.PrecisionsFine[i] + 1;
                    int startSubband = Tables.QuantUnitToCoeffIndex[i];
                    int endSubband = Tables.QuantUnitToCoeffIndex[i + 1];

                    for (int j = startSubband; j < endSubband; j++)
                    {
                        channel.QuantizedSpectraFine[j] = reader.ReadSignedInt(overflowBits);
                    }
                }
            }
        }

        private static void DecodeHuffmanValues(int[] spectrum, int index, int bandCount, HuffmanCodebook huff, int[] values)
        {
            int valueCount = bandCount >> huff.ValueCountPower;
            int mask = (1 << huff.ValueBits) - 1;

            for (int i = 0; i < valueCount; i++)
            {
                int value = values[i];
                for (int j = 0; j < huff.ValueCount; j++)
                {
                    spectrum[index++] = Bit.SignExtend32(value & mask, huff.ValueBits);
                    value >>= huff.ValueBits;
                }
            }
        }

        public static int ReadHuffmanValue(HuffmanCodebook huff, BitReader reader, bool signed = false)
        {
            int code = reader.PeekInt(huff.MaxBitSize);
            byte value = huff.Lookup[code];
            int bits = huff.Bits[value];
            reader.Position += bits;
            return signed ? Bit.SignExtend32(value, huff.ValueBits) : value;
        }

        private static void UnpackLfeBlock(BitReader reader, Block block)
        {
            Channel channel = block.Channels[0];
            block.QuantizationUnitCount = 2;

            DecodeLfeScaleFactors(reader, channel);
            CalculateLfePrecision(channel);
            channel.CodedQuantUnits = block.QuantizationUnitCount;
            ReadLfeSpectra(reader, channel);
        }

        private static void DecodeLfeScaleFactors(BitReader reader, Channel channel)
        {
            Array.Clear(channel.ScaleFactors, 0, channel.ScaleFactors.Length);
            for (int i = 0; i < channel.Block.QuantizationUnitCount; i++)
            {
                channel.ScaleFactors[i] = reader.ReadInt(5);
            }
        }

        private static void CalculateLfePrecision(Channel channel)
        {
            Block block = channel.Block;
            int precision = block.ReuseBandParams ? 8 : 4;
            for (int i = 0; i < block.QuantizationUnitCount; i++)
            {
                channel.Precisions[i] = precision;
                channel.PrecisionsFine[i] = 0;
            }
        }

        private static void ReadLfeSpectra(BitReader reader, Channel channel)
        {
            Array.Clear(channel.QuantizedSpectra, 0, channel.QuantizedSpectra.Length);

            for (int i = 0; i < channel.CodedQuantUnits; i++)
            {
                if (channel.Precisions[i] <= 0) continue;

                int precision = channel.Precisions[i] + 1;
                for (int j = Tables.QuantUnitToCoeffIndex[i]; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
                {
                    channel.QuantizedSpectra[j] = reader.ReadSignedInt(precision);
                }
            }
        }
    }
}
