using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace audiomedian
{
    class SuperWAV : IDisposable
    {

        public enum WavFormat
        {
            UNDEFINED_INVALID,
            WAVE,
            WAVE64,
            RF64,
            CUBASE_BIGFILE
        }

        WavFormat wavFormat = WavFormat.UNDEFINED_INVALID;
        //bool writingAllowed = false;
        FileStream fs;
        BinaryReader br;
        BinaryWriter bw;

        byte[] WAVE64_GUIDFOURCC_RIFF_LAST12 = new byte[12] { 0x2e, 0x91, 0xcf, 0x11, 0xa5, 0xd6, 0x28, 0xdb, 0x04, 0xc1, 0x00, 0x00 };
        byte[] WAVE64_GUIDFOURCC_LAST12 = new byte[12] { 0xf3, 0xac, 0xd3, 0x11, 0x8c, 0xd1, 0x00, 0xc0, 0x4f, 0x8e, 0xdb, 0x8a };
        const UInt64 WAVE64_SIZE_DIFFERENCE = 24; // This is the size of the 128 bit fourcc code and the 64 bit size field that are part of the size parameter itself in Wave64
        UInt32 RF64_MINUS1_VALUE = BitConverter.ToUInt32(new byte[4] { 0xFF, 0xFF, 0xFF, 0xFF }, 0);

        // Helpers for faster computation during format conversions:
        double INT32_MINVAL_ABS_DOUBLE = Math.Abs((double)Int32.MinValue);
        double INT16_MINVAL_ABS_DOUBLE = Math.Abs((double)Int16.MinValue);
        double INT8_MINVAL_ABS_DOUBLE = Math.Abs((double)sbyte.MinValue);

        struct ChunkInfo
        {
            public string name;
            public UInt64 size;
            public bool isValidWave64LegacyRIFFCode;
        }

        public enum AudioFormat
        {
            LPCM = 1,
            FLOAT = 3,
            WAVE_FORMAT_EXTENSIBLE = 65534 // I'm not 100% confident about this one. It works, but I'm not sure why RF64 doesn't just use the normal value for FLOAT. Maybe an error that ffmpeg makes?
        }

        public struct WavInfo
        {
            public UInt32 sampleRate;
            public UInt16 channelCount;
            public AudioFormat audioFormat; // We only support uncompressed = 1 for now
            public UInt32 byteRate;
            public UInt16 bitsPerSample;
            public UInt16 bytesPerTick;
            public UInt64 dataOffset;
            public UInt64 dataLength;
        }

        WavInfo wavInfo;

        // Helper variables to speed up things
        UInt16 bytesPerSample;
        UInt64 dataLengthInTicks;

        public UInt64 DataLengthInTicks
        {
            get
            {
                return dataLengthInTicks;
            }
        }

        public enum OpenMode
        {
            OPEN_FOR_READ,
            CREATE_FOR_READ_WRITE,
            CREATE_OR_OPEN_FOR_READ_WRITE
        }

        OpenMode openMode = OpenMode.OPEN_FOR_READ;

        // Constructor for reading
        public SuperWAV(string path)
        {
            openMode = OpenMode.OPEN_FOR_READ;


            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            br = new BinaryReader(fs);

            wavFormat = detectWavFormat();

            if (wavFormat != WavFormat.WAVE && wavFormat != WavFormat.WAVE64 && wavFormat != WavFormat.RF64)
            {
                throw new Exception("Only normal WAV and WAVE64 and RF64 is supported so far, not anything else.");
            }

            wavInfo = readWavInfo();

            if (wavInfo.audioFormat != AudioFormat.LPCM && wavInfo.audioFormat != AudioFormat.FLOAT)
            {
                throw new Exception("Only uncompressed WAV currently supported.");
            }

            // Sanity checks
            if (wavInfo.bitsPerSample * wavInfo.channelCount / 8 != wavInfo.bytesPerTick)
            {
                throw new Exception("Uhm what?");
            }
            else if (wavInfo.byteRate != wavInfo.sampleRate * wavInfo.bytesPerTick)
            {
                throw new Exception("Uhm what?");
            }

            bytesPerSample = (UInt16)(wavInfo.bitsPerSample / 8U);
            dataLengthInTicks = wavInfo.dataLength / wavInfo.bytesPerTick;

        }

        // Constructor for writing
        public SuperWAV(string path, WavFormat wavFormatForWritingA, UInt32 sampleRateA, UInt16 channelCountA, AudioFormat audioFormatA, UInt16 bitsPerSampleA, UInt64 initialDataLengthInTicks = 0)
        {
            openMode = OpenMode.CREATE_FOR_READ_WRITE;

            fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            br = new BinaryReader(fs);
            bw = new BinaryWriter(fs);

            bytesPerSample = (UInt16)(bitsPerSampleA / 8);
            dataLengthInTicks = initialDataLengthInTicks;

            wavInfo.sampleRate = sampleRateA;
            wavInfo.channelCount = channelCountA;
            wavInfo.audioFormat = audioFormatA;
            wavInfo.bitsPerSample = bitsPerSampleA;
            wavInfo.bytesPerTick = (UInt16)(bytesPerSample * channelCountA);
            wavInfo.dataLength = initialDataLengthInTicks * wavInfo.bytesPerTick;
            wavInfo.byteRate = wavInfo.sampleRate * wavInfo.bytesPerTick;

            wavFormat = wavFormatForWritingA;

            writeFileHusk(wavFormatForWritingA, ref wavInfo);

        }

        // Increase size of data chunk if necessary.
        public void checkAndIncreaseDataSize(UInt64 requiredDataSizeInTicks)
        {
            checkClosed();

            if (openMode == OpenMode.OPEN_FOR_READ)
            {
                throw new Exception("Trying to manipulate file that was opened for reading only!");
            }
            else if (openMode == OpenMode.CREATE_OR_OPEN_FOR_READ_WRITE)
            {
                throw new Exception("Modifying existing files is not yet implemented.");
            }
            else if (openMode == OpenMode.CREATE_FOR_READ_WRITE)
            {
                if (wavFormat == WavFormat.WAVE)
                {
                    if (requiredDataSizeInTicks > dataLengthInTicks)
                    {
                        wavInfo.dataLength = requiredDataSizeInTicks * wavInfo.bytesPerTick;

                        if (wavInfo.dataLength > UInt32.MaxValue)
                        {
                            throw new Exception("Trying to allocate more than 4GB of data in traditional wav file.");
                        }

                        bw.BaseStream.Seek((Int64)wavInfo.dataOffset, SeekOrigin.Begin);
                        bw.BaseStream.Seek((Int64)wavInfo.dataLength /*-1*/, SeekOrigin.Current);
                        bw.Write((byte)0);
                        Int64 currentPosition = bw.BaseStream.Position;
                        bw.Seek(4, SeekOrigin.Begin);
                        bw.Write((UInt32)currentPosition - 8);  // Check if -8 is actually correct
                        bw.BaseStream.Seek((Int64)wavInfo.dataOffset - (Int64)4, SeekOrigin.Begin);
                        bw.Write((UInt32)wavInfo.dataLength);
                        dataLengthInTicks = requiredDataSizeInTicks;
                    }
                }
                else if (wavFormat == WavFormat.WAVE64)
                {
                    if (requiredDataSizeInTicks > dataLengthInTicks)
                    {
                        wavInfo.dataLength = requiredDataSizeInTicks * wavInfo.bytesPerTick;

                        bw.BaseStream.Seek((Int64)wavInfo.dataOffset, SeekOrigin.Begin);
                        bw.BaseStream.Seek((Int64)wavInfo.dataLength /*-1*/, SeekOrigin.Current);
                        bw.Write((byte)0);
                        Int64 currentPosition = bw.BaseStream.Position;
                        bw.Seek(4 + 12, SeekOrigin.Begin);
                        bw.Write((UInt64)currentPosition);
                        bw.BaseStream.Seek((Int64)wavInfo.dataOffset - (Int64)8, SeekOrigin.Begin);
                        bw.Write((UInt64)wavInfo.dataLength + WAVE64_SIZE_DIFFERENCE);
                        dataLengthInTicks = requiredDataSizeInTicks;
                    }
                }
                else if (wavFormat == WavFormat.RF64)
                {
                    throw new Exception("Writing RF64 is not yet implemented.");
                }
                else
                {
                    throw new Exception("Whut? " + wavFormat + "? What do you mean by " + wavFormat + "?");
                }

            }
        }

        // Write the bare minimum for a working file.
        private void writeFileHusk(WavFormat wavFormatA, ref WavInfo wavInfoA)
        {
            checkClosed();

            if (openMode == OpenMode.CREATE_FOR_READ_WRITE)
            {

                if (wavFormatA == WavFormat.WAVE)
                {
                    bw.Seek(0, SeekOrigin.Begin);
                    bw.Write("RIFF".ToCharArray());
                    bw.Write((UInt32)0);
                    bw.Write("WAVE".ToCharArray());
                    bw.Write("fmt ".ToCharArray());
                    bw.Write((UInt32)16);
                    bw.Write((UInt16)wavInfoA.audioFormat);
                    bw.Write((UInt16)wavInfoA.channelCount);
                    bw.Write((UInt32)wavInfoA.sampleRate);
                    bw.Write((UInt32)wavInfoA.byteRate);
                    bw.Write((UInt16)wavInfoA.bytesPerTick);
                    bw.Write((UInt16)wavInfoA.bitsPerSample);
                    bw.Write("data".ToCharArray());
                    bw.Write((UInt32)wavInfoA.dataLength);
                    wavInfoA.dataOffset = (UInt64)bw.BaseStream.Position;
                    bw.BaseStream.Seek((Int64)wavInfoA.dataLength/*-1*/, SeekOrigin.Current);
                    bw.Write((byte)0);
                    Int64 currentPosition = bw.BaseStream.Position;
                    bw.Seek(4, SeekOrigin.Begin);
                    bw.Write((UInt32)currentPosition - 8); // TODO Check if -8 is actually correct



                }
                else if (wavFormat == WavFormat.WAVE64)
                {


                    bw.Seek(0, SeekOrigin.Begin);
                    bw.Write("riff".ToCharArray());
                    bw.Write(WAVE64_GUIDFOURCC_RIFF_LAST12);
                    bw.Write((UInt64)0);
                    bw.Write("wave".ToCharArray());
                    bw.Write(WAVE64_GUIDFOURCC_LAST12);
                    bw.Write("fmt ".ToCharArray());
                    bw.Write(WAVE64_GUIDFOURCC_LAST12);
                    bw.Write((UInt64)(16 + WAVE64_SIZE_DIFFERENCE));
                    bw.Write((UInt16)wavInfoA.audioFormat);
                    bw.Write((UInt16)wavInfoA.channelCount);
                    bw.Write((UInt32)wavInfoA.sampleRate);
                    bw.Write((UInt32)wavInfoA.byteRate);
                    bw.Write((UInt16)wavInfoA.bytesPerTick);
                    bw.Write((UInt16)wavInfoA.bitsPerSample);
                    bw.Write("data".ToCharArray());
                    bw.Write(WAVE64_GUIDFOURCC_LAST12);
                    bw.Write((UInt64)wavInfoA.dataLength + WAVE64_SIZE_DIFFERENCE);
                    wavInfoA.dataOffset = (UInt64)bw.BaseStream.Position;
                    bw.BaseStream.Seek((Int64)wavInfoA.dataLength /*-1*/, SeekOrigin.Current);
                    bw.Write((byte)0);
                    Int64 currentPosition = bw.BaseStream.Position;
                    bw.Seek(4 + 12, SeekOrigin.Begin);
                    bw.Write((UInt64)currentPosition);
                }
                else if (wavFormat == WavFormat.RF64)
                {
                    throw new Exception("Writing RF64 is not yet implemented.");
                }
                else
                {
                    throw new Exception("Whut? " + wavFormat + "? What do you mean by " + wavFormat + "?");
                }
            }
            else
            {
                throw new Exception("Trying to initialize an already existing file! Don't do that!");
            }
        }

        // TODO Optimize this more and find out how I can return by ref
        [Obsolete("Slow and won't work for giant files. Use getAs32BitFloatFast instead.")]
        public float[] getEntireFileAs32BitFloat()
        {
            checkClosed();

            float[] retVal = new float[wavInfo.channelCount * dataLengthInTicks];
            double[] tmp;
            for (UInt64 i = 0; i < dataLengthInTicks; i++)
            {
                tmp = this[i];
                for (uint c = 0; c < wavInfo.channelCount; c++)
                {

                    retVal[i * wavInfo.channelCount + c] = (float)tmp[c];
                }
            }
            return retVal;
        }

        // endIndex is inclusive
        // TODO check bounds, not only here but in all getters
        // OBSOLETE. Use fast function instead. (soon)
        [Obsolete("This is very slow. Use getAs32BitFloatFast instead")]
        public float[] getAs32BitFloat(UInt64 startIndex, UInt64 endIndex)
        {
            checkClosed();

            UInt64 ticksToServe = (1 + endIndex - startIndex);
            float[] retVal = new float[wavInfo.channelCount * ticksToServe];
            double[] tmp;
            for (UInt64 i = 0; i < ticksToServe; i++)
            {
                tmp = this[i + startIndex];
                for (uint c = 0; c < wavInfo.channelCount; c++)
                {

                    retVal[i * wavInfo.channelCount + c] = (float)tmp[c];
                }
            }
            return retVal;
        }

        // Use this instead of the overloaded [] operator if you need speed and need to get more than a single sample!
        // Untested!
        public float[] getAs32BitFloatFast(UInt64 startIndex, UInt64 endIndex)
        {
            checkClosed();

            UInt64 ticksToServe = (1 + endIndex - startIndex);
            float[] retVal = new float[wavInfo.channelCount * ticksToServe];

            UInt64 bytesToServe = ticksToServe * wavInfo.bytesPerTick;
            UInt64 firstByteToServe = startIndex * wavInfo.bytesPerTick;
            UInt64 firstByteToServeAbsolute = firstByteToServe + wavInfo.dataOffset;

            br.BaseStream.Seek((Int64)firstByteToServeAbsolute, SeekOrigin.Begin);
            byte[] dataAsBytes = br.ReadBytes((int)bytesToServe);

            if((ulong)dataAsBytes.Length < bytesToServe) // In case an uncareful application (wink wink) tries to read past the end of the file
            {
                if (dataAsBytes.Length == 0)
                {
                    return new float[0]; // Signal to the caller that he's way out of his depth
                }
                else
                {
                    bytesToServe = (ulong)(dataAsBytes.Length - dataAsBytes.Length% wavInfo.bytesPerTick); // If there's any data there at the end that isn't a multiple of bytespertick, yeet it
                }
            }

            UInt64 tmpNumber;

            switch (wavInfo.bitsPerSample)
            {
                case 8: // UNTESTED
                    for (UInt64 i = 0; i < bytesToServe; i++)
                    {
                        retVal[i] = (float)(((double)dataAsBytes[i] - 128.0) / INT8_MINVAL_ABS_DOUBLE);
                    }
                    break;
                case 16: // TESTED
                    Int16[] tmp0 = new Int16[bytesToServe / 2];
                    Buffer.BlockCopy(dataAsBytes, 0, tmp0, 0, (int)bytesToServe);
                    tmpNumber = (UInt64)tmp0.Length;
                    for (UInt64 i = 0; i < tmpNumber; i++)
                    {
                        retVal[i] = (float)((double)tmp0[i] / INT16_MINVAL_ABS_DOUBLE);
                    }
                    break;
                case 32:// UNTESTED
                    if (wavInfo.audioFormat == AudioFormat.FLOAT) // Most straightforward!
                    {
                        Buffer.BlockCopy(dataAsBytes, 0, retVal, 0, (int)bytesToServe);
                    }
                    else
                    { // UNTESTED
                        Int32[] tmp1 = new Int32[bytesToServe / 4];
                        Buffer.BlockCopy(dataAsBytes, 0, tmp1, 0, (int)bytesToServe);
                        tmpNumber = (UInt64)tmp1.Length;
                        for (UInt64 i = 0; i < tmpNumber; i++)
                        {
                            retVal[i] = (float)((double)tmp1[i] / INT32_MINVAL_ABS_DOUBLE);
                        }
                    }
                    break;
                case 24:// UNTESTED
                    Int32[] singleOne = new Int32[1] { 0 };
                    tmpNumber = bytesToServe / 3;
                    for (UInt64 i = 0; i < tmpNumber; i++)
                    {
                        Buffer.BlockCopy(dataAsBytes, (int)(i * 3), singleOne, 1, 3);
                        retVal[i] = (float)((double)singleOne[0] / Math.Abs((double)Int32.MinValue));
                    }
                    break;
            }

            return retVal;

        }

        [Obsolete("This is very slow. Use writeFloatArrayFast instead")]
        public void writeFloatArray(float[] dataToAdd, UInt64 offset = 0)
        {
            checkClosed();

            checkAndIncreaseDataSize((UInt64)dataToAdd.Length / wavInfo.channelCount + offset);

            UInt64 dataToAddLengthInTicks = (UInt64)dataToAdd.Length / (UInt64)wavInfo.channelCount;
            double[] tmp = new double[wavInfo.channelCount];
            for (UInt64 i = 0; i < dataToAddLengthInTicks; i++)
            {
                for (uint c = 0; c < wavInfo.channelCount; c++)
                {
                    tmp[c] = dataToAdd[i * wavInfo.channelCount + c];
                }
                this[offset + i] = tmp;
            }
        }
        public void writeFloatArrayFast(float[] dataToAdd, UInt64 offset = 0)
        {
            checkClosed();

            if (openMode == OpenMode.OPEN_FOR_READ)
            {
                throw new Exception("Trying to edit file that was opened for reading only!");
            }
            else if (openMode == OpenMode.CREATE_OR_OPEN_FOR_READ_WRITE)
            {
                throw new Exception("Modifying existing files is not yet implemented.");
            }
            else if (openMode == OpenMode.CREATE_FOR_READ_WRITE)
            {
                UInt64 dataToAddLength = (UInt64)dataToAdd.Length;
                checkAndIncreaseDataSize(dataToAddLength / wavInfo.channelCount + offset);

                UInt64 dataToAddLengthInTicks = (UInt64)dataToAdd.Length / (UInt64)wavInfo.channelCount;
                UInt64 bytesToWrite = dataToAddLengthInTicks * wavInfo.bytesPerTick;
                UInt64 firstByteToWrite = offset * wavInfo.bytesPerTick;
                UInt64 firstByteToWriteAbsolute = firstByteToWrite + wavInfo.dataOffset;
                byte[] writeBuffer = new byte[bytesToWrite];


                switch (wavInfo.bitsPerSample)
                {
                    case 8: // UNTESTED
                        for (UInt64 i = 0; i < dataToAddLength; i++)
                        {
                            writeBuffer[i] = (byte)(Math.Min(sbyte.MaxValue, Math.Max(sbyte.MinValue, dataToAdd[i] * INT8_MINVAL_ABS_DOUBLE)) + 128.0);
                        }
                        break;
                    case 16: // UNTESTED
                        Int16[] tmp0 = new Int16[writeBuffer.Length / 2];
                        for (UInt64 i = 0; i < dataToAddLength; i++)
                        {
                            tmp0[i] = (Int16)(Math.Min(Int16.MaxValue, Math.Max(Int16.MinValue, dataToAdd[i] * INT16_MINVAL_ABS_DOUBLE)));
                        }
                        Buffer.BlockCopy(tmp0, 0, writeBuffer, 0, (int)bytesToWrite);
                        break;
                    case 32: // UNTESTED
                        if (wavInfo.audioFormat == AudioFormat.FLOAT)
                        {
                            Buffer.BlockCopy(dataToAdd, 0, writeBuffer, 0, (int)bytesToWrite);
                        }
                        else
                        {
                            Int32[] tmp1 = new Int32[writeBuffer.Length / 4];
                            for (UInt64 i = 0; i < dataToAddLength; i++)
                            {
                                tmp1[i] = (Int32)(Math.Min(Int32.MaxValue, Math.Max(Int32.MinValue, dataToAdd[i] * INT32_MINVAL_ABS_DOUBLE)));
                            }
                            Buffer.BlockCopy(tmp1, 0, writeBuffer, 0, (int)bytesToWrite);
                        }
                        break;
                    case 24: // UNTESTED
                        Int32[] tmp3 = new Int32[1];
                        for (UInt64 i = 0; i < dataToAddLength; i++)
                        {
                            tmp3[0] = (Int32)(Math.Min(Int32.MaxValue, Math.Max(Int32.MinValue, dataToAdd[i] * Math.Abs((double)Int32.MinValue))));
                            Buffer.BlockCopy(tmp3, 1, writeBuffer, (int)(i * 3), 3);
                        }
                        break;
                }

                bw.BaseStream.Seek((Int64)firstByteToWriteAbsolute, SeekOrigin.Begin);
                bw.Write(writeBuffer, 0, (int)bytesToWrite);
            }

        }

        public WavInfo getWavInfo()
        {
            checkClosed();
            return wavInfo;
        }


        // test: (UInt32)(((double)(UInt32.MaxValue - 2)/ (double)UInt32.MaxValue)*(double)UInt32.MaxValue)
        public double[] this[UInt64 index]
        {
            get
            {

                checkClosed();


                double[] retVal = new double[wavInfo.channelCount];

                UInt64 baseOffset = wavInfo.dataOffset + index * wavInfo.bytesPerTick;
                byte[] readBuffer;
                br.BaseStream.Seek((Int64)baseOffset, SeekOrigin.Begin);

                readBuffer = br.ReadBytes(wavInfo.bytesPerTick);

                switch (wavInfo.bitsPerSample)
                {
                    case 8:
                        for (int i = 0; i < wavInfo.channelCount; i++)
                        {
                            retVal[i] = (double)(((double)readBuffer[i] - 128.0) / INT8_MINVAL_ABS_DOUBLE);
                        }
                        break;
                    case 16:
                        Int16[] tmp0 = new Int16[wavInfo.channelCount];
                        Buffer.BlockCopy(readBuffer, 0, tmp0, 0, wavInfo.bytesPerTick);
                        for (int i = 0; i < wavInfo.channelCount; i++)
                        {
                            retVal[i] = (double)((double)tmp0[i] / INT16_MINVAL_ABS_DOUBLE);
                        }
                        break;
                    case 32:
                        if (wavInfo.audioFormat == AudioFormat.FLOAT)
                        {
                            float[] tmp1 = new float[wavInfo.channelCount];
                            Buffer.BlockCopy(readBuffer, 0, tmp1, 0, wavInfo.bytesPerTick);
                            for (int i = 0; i < wavInfo.channelCount; i++)
                            {
                                retVal[i] = (double)tmp1[i];
                            }
                        }
                        else
                        {
                            Int32[] tmp2 = new Int32[wavInfo.channelCount];
                            Buffer.BlockCopy(readBuffer, 0, tmp2, 0, wavInfo.bytesPerTick);
                            for (int i = 0; i < wavInfo.channelCount; i++)
                            {
                                retVal[i] = (double)((double)tmp2[i] / INT32_MINVAL_ABS_DOUBLE);
                            }
                        }
                        break;
                    // Test:
                    // Int16[] abc = new Int16[1]{Int16.MaxValue};Int32[] hah = new Int32[1]{0}; Buffer.BlockCopy(abc,0,hah,0,2); hah[0] // bad
                    // Int16[] abc = new Int16[1]{Int16.MinValue};Int32[] hah = new Int32[1]{0}; Buffer.BlockCopy(abc,0,hah,0,2); hah[0] // bad
                    // Int16[] abc = new Int16[1]{Int16.MaxValue};Int32[] hah = new Int32[1]{0}; Buffer.BlockCopy(abc,0,hah,2,2); hah[0] //correctly scaled
                    // Int16[] abc = new Int16[1]{Int16.MinValue};Int32[] hah = new Int32[1]{0}; Buffer.BlockCopy(abc,0,hah,2,2); hah[0] //correctly scaled
                    case 24: // Untested
                        Int32[] singleOne = new Int32[1] { 0 }; // We just interpret as Int32 and ignore one byte.
                        for (int i = 0; i < wavInfo.channelCount; i++)
                        {
                            Buffer.BlockCopy(readBuffer, i * 3, singleOne, 1, 3);
                            retVal[i] = (double)((double)singleOne[0] / INT32_MINVAL_ABS_DOUBLE);
                        }
                        break;
                }
                /*for (uint i = 0; i < wavInfo.channelCount; i++)
                {
                    offset = baseOffset + i * bytesPerSample;
                    readBuffer = br.ReadBytes(bytesPerSample);
                    
                }*/

                return retVal;
            }

            set
            {
                checkClosed();


                if (value.Length != wavInfo.channelCount)
                {
                    throw new Exception("Data array supplied for writing does not match channel count.");
                }
                if (openMode == OpenMode.OPEN_FOR_READ)
                {
                    throw new Exception("Trying to edit file that was opened for reading only!");
                }
                else if (openMode == OpenMode.CREATE_OR_OPEN_FOR_READ_WRITE)
                {
                    throw new Exception("Modifying existing files is not yet implemented.");
                }
                else if (openMode == OpenMode.CREATE_FOR_READ_WRITE)
                {
                    UInt64 startOffset = index * wavInfo.bytesPerTick;
                    //UInt64 endOffset = startOffset + wavInfo.bytesPerTick -1;
                    checkAndIncreaseDataSize(index);
                    UInt64 startOffsetAbsolute = wavInfo.dataOffset + startOffset;

                    byte[] dataToWrite = new byte[wavInfo.bytesPerTick];

                    switch (wavInfo.bitsPerSample)
                    {
                        case 8:
                            for (int i = 0; i < wavInfo.channelCount; i++)
                            {
                                dataToWrite[i] = (byte)(Math.Min(sbyte.MaxValue, Math.Max(sbyte.MinValue, value[i] * INT8_MINVAL_ABS_DOUBLE)) + 128.0);
                            }
                            break;
                        case 16:
                            Int16[] tmp0 = new Int16[wavInfo.channelCount];
                            for (int i = 0; i < wavInfo.channelCount; i++)
                            {
                                tmp0[i] = (Int16)(Math.Min(Int16.MaxValue, Math.Max(Int16.MinValue, value[i] * INT16_MINVAL_ABS_DOUBLE)));
                            }
                            Buffer.BlockCopy(tmp0, 0, dataToWrite, 0, dataToWrite.Length);
                            break;
                        case 32:
                            if (wavInfo.audioFormat == AudioFormat.FLOAT)
                            {
                                float[] tmp1 = new float[wavInfo.channelCount];
                                for (int i = 0; i < wavInfo.channelCount; i++)
                                {
                                    tmp1[i] = (float)value[i];
                                }
                                Buffer.BlockCopy(tmp1, 0, dataToWrite, 0, dataToWrite.Length);
                            }
                            else
                            {
                                Int32[] tmp2 = new Int32[wavInfo.channelCount];
                                for (int i = 0; i < wavInfo.channelCount; i++)
                                {
                                    tmp2[i] = (Int32)(Math.Min(Int32.MaxValue, Math.Max(Int32.MinValue, value[i] * INT32_MINVAL_ABS_DOUBLE)));
                                }
                                Buffer.BlockCopy(tmp2, 0, dataToWrite, 0, dataToWrite.Length);
                            }
                            break;
                        case 24:
                            Int32[] tmp3 = new Int32[1];
                            for (int i = 0; i < wavInfo.channelCount; i++)
                            {
                                tmp3[0] = (Int32)(Math.Min(Int32.MaxValue, Math.Max(Int32.MinValue, value[i] * INT32_MINVAL_ABS_DOUBLE)));
                                Buffer.BlockCopy(tmp3, 1, dataToWrite, i * 3, 3);
                            }
                            break;
                    }

                    bw.BaseStream.Seek((Int64)startOffsetAbsolute, SeekOrigin.Begin);
                    bw.Write(dataToWrite);
                }
            }
        }


        private WavFormat detectWavFormat()
        {
            checkClosed();

            ChunkInfo chunk = readChunk32(0);
            if (chunk.name == "RIFF")
            {
                // Either Wave64 or normal WAV
                chunk = readChunk32(12);
                if (chunk.name == "FMT " && chunk.size >= 16)
                {
                    // Probably normal wav?
                    return WavFormat.WAVE;
                }
                else
                {
                    chunk = readChunkWave64(40);
                    if (chunk.name == "FMT " && chunk.size >= 16 && chunk.isValidWave64LegacyRIFFCode)
                    {
                        // Probably wave64? But need to properly read specification to make sure. Just based on hexeditor.
                        return WavFormat.WAVE64;
                    }
                }
            }
            else if (chunk.name == "RF64")
            {
                chunk = readChunk32(12);
                if (chunk.name == "DS64")
                {
                    // RF64
                    return WavFormat.RF64;
                }
            }

            // If nothing else returns something valid, we failed at detecting.
            return WavFormat.UNDEFINED_INVALID;
        }


        private WavInfo readWavInfo()
        {
            checkClosed();

            WavInfo retVal = new WavInfo();
            if (wavFormat == WavFormat.WAVE)
            {

                UInt64 fmtChunkLength = 0;

                // find fmt chunk
                ChunkInfo chunk = new ChunkInfo();
                UInt64 currentPosition = 12;
                UInt64 resultPosition;
                do
                {
                    chunk = readChunk32(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 8 + chunk.size;

                } while (chunk.name != "FMT ");

                fmtChunkLength = chunk.size;

                br.BaseStream.Seek((Int64)(resultPosition + (UInt64)8), SeekOrigin.Begin);


                retVal.audioFormat = (AudioFormat)br.ReadUInt16();
                retVal.channelCount = br.ReadUInt16();
                retVal.sampleRate = br.ReadUInt32();
                retVal.byteRate = br.ReadUInt32();
                retVal.bytesPerTick = br.ReadUInt16();
                retVal.bitsPerSample = br.ReadUInt16();

                // WAVE Extensible handling
                if (retVal.audioFormat == AudioFormat.WAVE_FORMAT_EXTENSIBLE)
                {
                    if (fmtChunkLength > 16)
                    {
                        UInt16 restChunkLength = br.ReadUInt16(); // This is a guess!
                        if (restChunkLength >= 8)
                        {
                            _ = br.ReadUInt16(); // This appears to be once again bits per sample.
                            _ = br.ReadUInt32(); // Channel mask. Irrelevant for us.
                            retVal.audioFormat = (AudioFormat)br.ReadUInt16(); // Here we go.
                            // The rest of the fmt chunk is a GUID thingie, not interesting.

                        }
                        else
                        {
                            throw new Exception("Weird fmt chunk");
                        }
                    }
                    else
                    {
                        throw new Exception("Weird fmt chunk");
                    }
                }


                // find data chunk
                currentPosition = 12;
                do
                {
                    chunk = readChunk32(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 8 + chunk.size;

                } while (chunk.name != "DATA");

                retVal.dataOffset = resultPosition + 8;
                retVal.dataLength = chunk.size;

            }
            else if (wavFormat == WavFormat.WAVE64) // Todo: respect 8 byte boundaries.
            {
                // find fmt chunk
                ChunkInfo chunk = new ChunkInfo();
                UInt64 currentPosition = 40;
                UInt64 resultPosition;
                do
                {
                    if (currentPosition % 8 != 0)
                    {
                        currentPosition = ((currentPosition / 8) + 1) * 8; // Need to remember that wave64 stuff always needs to be aligned on 8 byte boundaries!
                    }
                    chunk = readChunkWave64(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 24 + chunk.size;

                } while (chunk.name != "FMT " || !chunk.isValidWave64LegacyRIFFCode);

                br.BaseStream.Seek((Int64)(resultPosition + (UInt64)24), SeekOrigin.Begin);

                //br.BaseStream.Seek(64, SeekOrigin.Begin);
                retVal.audioFormat = (AudioFormat)br.ReadUInt16();
                retVal.channelCount = br.ReadUInt16();
                retVal.sampleRate = br.ReadUInt32();
                retVal.byteRate = br.ReadUInt32();
                retVal.bytesPerTick = br.ReadUInt16();
                retVal.bitsPerSample = br.ReadUInt16();


                // find data chunk
                currentPosition = 40;
                do
                {
                    if (currentPosition % 8 != 0)
                    {
                        currentPosition = ((currentPosition / 8) + 1) * 8; // Need to remember that wave64 stuff always needs to be aligned on 8 byte boundaries!
                    }
                    chunk = readChunkWave64(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 24 + chunk.size;

                } while (chunk.name != "DATA" || !chunk.isValidWave64LegacyRIFFCode);

                retVal.dataOffset = resultPosition + 24;
                retVal.dataLength = chunk.size;
            }
            else if (wavFormat == WavFormat.RF64)
            {
                br.BaseStream.Seek(20, SeekOrigin.Begin);

                UInt64 ds64_riffSize = br.ReadUInt64();
                UInt64 ds64_dataSize = br.ReadUInt64();

                UInt64 fmtChunkLength = 0;

                // find fmt chunk
                ChunkInfo chunk = new ChunkInfo();
                UInt64 currentPosition = 12;
                UInt64 resultPosition;
                do
                {
                    chunk = readChunk32(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 8 + chunk.size;

                } while (chunk.name != "FMT ");

                fmtChunkLength = chunk.size;

                br.BaseStream.Seek((Int64)(resultPosition + (UInt64)8), SeekOrigin.Begin);

                // read fmt chunk data, as usual
                retVal.audioFormat = (AudioFormat)br.ReadUInt16();
                retVal.channelCount = br.ReadUInt16();
                retVal.sampleRate = br.ReadUInt32();
                retVal.byteRate = br.ReadUInt32();
                retVal.bytesPerTick = br.ReadUInt16();
                retVal.bitsPerSample = br.ReadUInt16();

                // WAVE Extensible handling
                if (retVal.audioFormat == AudioFormat.WAVE_FORMAT_EXTENSIBLE)
                {
                    if (fmtChunkLength > 16)
                    {
                        UInt16 restChunkLength = br.ReadUInt16(); // This is a guess!
                        if (restChunkLength >= 8)
                        {
                            _ = br.ReadUInt16(); // This appears to be once again bits per sample.
                            _ = br.ReadUInt32(); // Channel mask. Irrelevant for us.
                            retVal.audioFormat = (AudioFormat)br.ReadUInt16(); // Here we go.
                            // The rest of the fmt chunk is a GUID thingie, not interesting.

                        }
                        else
                        {
                            throw new Exception("Weird fmt chunk");
                        }
                    }
                    else
                    {
                        throw new Exception("Weird fmt chunk");
                    }
                }



                // find data chunk
                currentPosition = 12;
                do
                {
                    chunk = readChunk32(currentPosition); // TODO gracefully handle error if no data chunk exists. Currently would crash.
                    resultPosition = currentPosition;
                    currentPosition += 8 + chunk.size;

                } while (chunk.name != "DATA");

                retVal.dataOffset = resultPosition + 8;
                retVal.dataLength = chunk.size == RF64_MINUS1_VALUE ? ds64_dataSize : chunk.size; // According to specification, we must read the size from this chunk unless it's FFFFFFFF (or -1 if interpreted as signed Int32)

            }
            else
            {
                // Not supported (yet)
            }
            return retVal;
        }

        private ChunkInfo readChunk32(UInt64 position)
        {
            checkClosed();

            br.BaseStream.Seek((Int64)position, SeekOrigin.Begin);
            ChunkInfo retVal = new ChunkInfo();
            byte[] nameBytes = br.ReadBytes(4);
            retVal.name = Encoding.ASCII.GetString(nameBytes).ToUpper();
            retVal.size = br.ReadUInt32();
            return retVal;
        }


        private ChunkInfo readChunkWave64(UInt64 position)
        {
            checkClosed();

            /*if(position % 8 != 0)
            {
                position = ((position / 8) + 1) * 8; // Need to remember that wave64 stuff always needs to be aligned on 8 byte boundaries!
            }*/ // Decided not to do that here because its too convenient and will introduce sneaky errors in the end ... better to catch it right in teh code.

            br.BaseStream.Seek((Int64)position, SeekOrigin.Begin);
            ChunkInfo retVal = new ChunkInfo();
            byte[] nameBytes = br.ReadBytes(4);
            byte[] fourCC = br.ReadBytes(12);
            retVal.isValidWave64LegacyRIFFCode = EqualBytesLongUnrolled(fourCC, WAVE64_GUIDFOURCC_LAST12);
            retVal.name = Encoding.ASCII.GetString(nameBytes).ToUpper();
            retVal.size = br.ReadUInt64() - (UInt64)24U;
            return retVal;
        }

        // from: https://stackoverflow.com/a/33307903
        static public unsafe bool EqualBytesLongUnrolled(byte[] data1, byte[] data2)
        {
            if (data1 == data2)
                return true;
            if (data1.Length != data2.Length)
                return false;

            fixed (byte* bytes1 = data1, bytes2 = data2)
            {
                int len = data1.Length;
                int rem = len % (sizeof(long) * 16);
                long* b1 = (long*)bytes1;
                long* b2 = (long*)bytes2;
                long* e1 = (long*)(bytes1 + len - rem);

                while (b1 < e1)
                {
                    if (*(b1) != *(b2) || *(b1 + 1) != *(b2 + 1) ||
                        *(b1 + 2) != *(b2 + 2) || *(b1 + 3) != *(b2 + 3) ||
                        *(b1 + 4) != *(b2 + 4) || *(b1 + 5) != *(b2 + 5) ||
                        *(b1 + 6) != *(b2 + 6) || *(b1 + 7) != *(b2 + 7) ||
                        *(b1 + 8) != *(b2 + 8) || *(b1 + 9) != *(b2 + 9) ||
                        *(b1 + 10) != *(b2 + 10) || *(b1 + 11) != *(b2 + 11) ||
                        *(b1 + 12) != *(b2 + 12) || *(b1 + 13) != *(b2 + 13) ||
                        *(b1 + 14) != *(b2 + 14) || *(b1 + 15) != *(b2 + 15))
                        return false;
                    b1 += 16;
                    b2 += 16;
                }

                for (int i = 0; i < rem; i++)
                    if (data1[len - 1 - i] != data2[len - 1 - i])
                        return false;

                return true;
            }
        }

        bool isClosed = false;

        private void checkClosed()
        {
            if (isClosed)
            {
                throw new Exception("Trying to work with a file that's already closed.");
            }
        }

        public void Dispose()
        {
            Close();
        }

        private void Close()
        {

            if (isClosed)
            {
                return; // Do nothing if already closed
            }
            if (openMode == OpenMode.OPEN_FOR_READ)
            {

                br.Dispose();
                fs.Close();
            }
            else
            {

                br.Dispose();
                bw.Dispose();
                fs.Close();
            }
            isClosed = true;
        }

        ~SuperWAV()
        {
            Close();
        }
    }
}
