﻿namespace lsif_debug
{
	internal static class ConsolePlus
	{
		private static List<string> LoggedErrors = new();
		private static List<string> LoggedWarnings = new();

		public static void WriteLine() => Console.WriteLine();
		public static void WriteLine(string content) => Console.WriteLine(content);

		public static void WriteWarning(string content)
		{
			LoggedWarnings.Add(content);
			WriteWarningCore(content);
		}

		public static void WriteError(string content)
		{
			LoggedErrors.Add(content);
			WriteErrorCore(content);
		}

		public static void WriteSuccess(string content)
		{
			var originalColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine("[Success] " + content);
			Console.ForegroundColor = originalColor;
		}

		public static void ReplayWarningsAndErrors()
		{
			if (LoggedWarnings.Count > 0)
			{
				Console.WriteLine("--------------- Warnings ---------------");
				Console.WriteLine();

				foreach (var warning in LoggedWarnings)
				{
					WriteWarningCore(warning);
				}
			}

			if (LoggedErrors.Count > 0)
			{
				Console.WriteLine("--------------- Errors ---------------");
				Console.WriteLine();

				foreach (var error in LoggedErrors)
				{
					WriteErrorCore(error);
				}
			}
		}

		private static void WriteWarningCore(string content)
		{
			var originalColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("[Warning] " + content);
			Console.ForegroundColor = originalColor;
		}

		private static void WriteErrorCore(string content)
		{
			var originalColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("[Error] " + content);
			Console.ForegroundColor = originalColor;
		}
	}
}
