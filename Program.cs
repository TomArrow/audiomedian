using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace audiomedian
{
    class Program
    {

        static string outputFile = "output.wav";

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


            var mixer = new MedianSampleProvider(readers.ToArray());
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
        }
    }
}
