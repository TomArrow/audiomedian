using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
//using NAudio.Wave;
//using NAudio.Wave.SampleProviders;

namespace audiomedian
{
    class Program
    {

        static string outputFile = "output.wav";

        const int bufferSize = 1024*1024;

        static void Main(string[] args)
        {

            List<string> inputFiles = new List<string>();
            
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-o")
                {
                    outputFile = args[++i];
                } else
                {
                    inputFiles.Add(args[i]);
                }


            }

            if (inputFiles.Count % 2 == 0)
            {
                Console.Write("Not technically median, since input file count isn't odd. Middle values will be averaged. \n");
                //Environment.Exit(0);
            }

            string[] inputs = inputFiles.ToArray();

            for (int i =0; i < inputs.Length; i++)
            {

                Console.Write("Input file "+((i+1).ToString())+" is " + inputs[i] + "\n");
            }

            Console.Write("Outputfile is " + outputFile + "\n");


            List<SuperWAV> readers = new List<SuperWAV>();

            ulong longestDuration = 0;
            int highestChannelCount = 0;

            for (int i = 0; i < inputs.Length; i++)
            {

                Console.Write("Trying to read " + inputs[i] + " ... \n");
                try
                {
                    SuperWAV thisReader = new SuperWAV(inputs[i]);
                    SuperWAV.WavInfo info = thisReader.getWavInfo();
                    ulong duration = info.dataLength / info.bytesPerTick;
                    if (duration > longestDuration)
                    {
                        longestDuration = duration;
                    }
                    if (info.channelCount > highestChannelCount)
                    {
                        highestChannelCount = info.channelCount;
                    }
                    readers.Add(thisReader);
                }catch (Exception err)
                {

                    Console.Write("Could not open " + inputs[i] + ", "+err.Message+", EXITING. \n");
                    Environment.Exit(0);
                }
            }

            SuperWAV.WavInfo firstSourceInfo = readers[0].getWavInfo();
            //int channelCount = firstSourceInfo.channelCount;
            SuperWAV outputWriter = new SuperWAV(outputFile, SuperWAV.WavFormat.WAVE64, firstSourceInfo.sampleRate, (ushort)highestChannelCount, firstSourceInfo.audioFormat, firstSourceInfo.bitsPerSample, 0/*longestDuration*/);

            float[][] readBuffers = new float[readers.Count][];

            ulong readBufferOffset = 0;
            ulong writeBufferOffset = 0;

            float[] outputBuffer = new float[bufferSize*highestChannelCount];

            ulong innerIndex = 0;
            for (ulong i = 0;i< longestDuration; i++, innerIndex++)
            {
                // We need a buffer otherwise it's just too damn slow. Talking minutes for a few megabytes... ridiculous. :D Gotta improve SuperWAV maybe.

                int readerIndex = 0; 
                if (i % bufferSize == 0)
                {
                    // Freshen up le buffers
                    foreach (SuperWAV reader in readers)
                    {
                        if (i < (reader.getWavInfo().dataLength / reader.getWavInfo().bytesPerTick))
                        {
                            readBuffers[readerIndex] = reader.getAs32BitFloatFast(i, i + bufferSize - 1);
                        }
                        readerIndex++;
                    }
                    readBufferOffset = i;


                    if (i != 0)
                    {
                        // Flush into file.
                        outputWriter.writeFloatArrayFast(outputBuffer, writeBufferOffset);
                        writeBufferOffset = i;
                    }
                }


                //List<double>[] samples = new List<double>[highestChannelCount];
                List<float>[] samples = new List<float>[highestChannelCount];
                for (int c = 0; c < highestChannelCount; c++)
                {
                    samples[c] = new List<float>();

                }
                //double[] outputSamples = new double[highestChannelCount];
                readerIndex = 0;
                foreach (SuperWAV reader in readers)
                {
                    if(i< (reader.getWavInfo().dataLength / reader.getWavInfo().bytesPerTick))
                    {
                        //double[] valuesHere = reader[i];
                        int bufferOffset = (int)(reader.getWavInfo().channelCount*(i - readBufferOffset));
                        float[] valuesHere = readBuffers[readerIndex];
                        for(int c = 0; c < highestChannelCount && c< valuesHere.Length; c++)
                        {
                            samples[c].Add(valuesHere[(int)(bufferOffset+c)]);
                        }
                    }
                    readerIndex++;
                }
                for (int c = 0; c < highestChannelCount; c++)
                {
                    samples[c].Sort();
                    int middleIndex = samples[c].Count / 2;
                    float valueHere = 0;
                    if (samples[c].Count % 2 == 0)
                    {
                        // Not technically median. Odd. Or rather, not odd. Can happen with files of differing lenghs or straight up not odd counts. Oh well.
                        // Just average the middle values
                        //outputSamples[c] = (samples[c][middleIndex - 1] + samples[c][middleIndex])/2.0;
                        valueHere = (samples[c][middleIndex - 1] + samples[c][middleIndex])/2.0f;
                    }
                    else
                    {

                        //outputSamples[c] = samples[c][middleIndex];
                        valueHere = samples[c][middleIndex];
                    }

                    outputBuffer[(int)((i- writeBufferOffset) *(ulong)highestChannelCount+(ulong)c)] = valueHere;
                }


                //outputWriter[i] = outputSamples;

            }

            int leftOver =(int)(innerIndex % bufferSize);
            if (leftOver > 0)
            {
                outputWriter.writeFloatArrayFast(outputBuffer, writeBufferOffset); // TODO Don't write more than necessary.
            }


            /*var mixer = new MyMedianSampleProvider(readers.ToArray());
            if (File.Exists(outputFile))
            {
                Console.Write("File " + outputFile + " already exists, EXITING. \n");
                Environment.Exit(0);
            }
            WaveFileWriter.CreateWaveFile(outputFile, new SampleToWaveProvider( mixer));

            for(int i = 0; i < readers.Count(); i++)
            {
                readers[i].Dispose();
            }*/
        }
        /*
        static void MainOldNAudio(string[] args)
        {

            List<string> inputFiles = new List<string>();
            
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-o")
                {
                    outputFile = args[++i];
                } else
                {
                    inputFiles.Add(args[i]);
                }


            }

            if (inputFiles.Count % 2 == 0)
            {
                Console.Write("Need odd number of input files, EXITING. \n");
                Environment.Exit(0);
            }

            string[] inputs = inputFiles.ToArray();

            for (int i =0; i < inputs.Length; i++)
            {

                Console.Write("Input file "+((i+1).ToString())+" is " + inputs[i] + "\n");
            }

            Console.Write("Outputfile is " + outputFile + "\n");


            List<AudioFileReader> readers = new List<AudioFileReader>();

            for (int i = 0; i < inputs.Length; i++)
            {

                Console.Write("Trying to read " + inputs[i] + " ... \n");
                try
                {

                    readers.Add(new AudioFileReader(inputs[i]));
                }catch (Exception err)
                {

                    Console.Write("Could not open " + inputs[i] + ", "+err.Message+", EXITING. \n");
                    Environment.Exit(0);
                }
            }


            var mixer = new MyMedianSampleProvider(readers.ToArray());
            if (File.Exists(outputFile))
            {
                Console.Write("File " + outputFile + " already exists, EXITING. \n");
                Environment.Exit(0);
            }
            WaveFileWriter.CreateWaveFile(outputFile, new SampleToWaveProvider( mixer));

            for(int i = 0; i < readers.Count(); i++)
            {
                readers[i].Dispose();
            }
        }*/
    }
}
