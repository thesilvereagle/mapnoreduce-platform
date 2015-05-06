﻿using System;
using ClientServices;

namespace UserApplicationSample
{
	public class Program
	{
		private static readonly string Help = Resources.HelpMessage;

		public static void Main(string[] args) {
			if (args.Length == 1)
				Console.Out.Write(Help);

			var entryUrl = args[1];
			var file = args[2];
			var ouput = args[3];
			var nSplits = Int32.Parse(args[4]);
			var mapClassName = args[5];
			var assemblyFilePath = args[6];

			ExecuteMapJob(entryUrl, file, ouput, nSplits, mapClassName, assemblyFilePath);
		}

		public static void ExecuteMapJob(string entryUrl, string filePath, string outputPath, int splits, string mapClassName, string assemblyFilePath) {
			Console.WriteLine("User Application, started with the following parameters:\n"
				+ "-EntryURL={0} -FilePath={1} -OutputPath={2} -Splits={3} -MapClassName={4} -AssemblyFilePath={5}",
				entryUrl, filePath, outputPath, splits, mapClassName, assemblyFilePath);

			var client = new ClientService();
			client.Init(entryUrl);
			client.Submit(filePath, splits, outputPath, mapClassName, assemblyFilePath);
		}
	}
}