﻿using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Console = lsif_linker.ConsolePlus;

internal class Program
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private static readonly char[] DirectoryPathSeparators = new[] { '/', '\\' };

	static async Task<int> Main(string[] args)
	{
		var cancellationToken = CancellationToken.None;
		var rootCommand = new RootCommand("Tool to link LSIF files with their original source.");
		var lsifArgument = new Argument<FileInfo>("lsif", "Path to a *.lsif file.");
		rootCommand.AddArgument(lsifArgument);
		var sourceArgument = new Argument<DirectoryInfo>("source", "Path to the local source that the LSIF was generated");
		rootCommand.AddArgument(sourceArgument);
		var output = new Option<FileInfo>("output", "File to output the linked LSIF to.");
		rootCommand.AddOption(output);

		rootCommand.SetHandler(() => rootCommand.InvokeAsync("-h"));
		rootCommand.SetHandler(async (FileInfo lsif, DirectoryInfo source, FileInfo? outputFile) =>
		{
			Console.WriteLine($"Linking '{lsif.FullName}' to '{source.FullName}'");
			Console.WriteLine();
			var linkedJson = ExtractAndLinkJson(lsif, source);
			var outputFilePath = TryResolveOutputFilePath(lsif, outputFile);
			if (outputFilePath is null)
			{
				return;
			}

			await SerializeAsync(linkedJson, outputFilePath, cancellationToken);

			Console.WriteLine();
			Console.WriteSuccess($"Successfully linked lsif! File: '{outputFilePath}'.");
		}, lsifArgument, sourceArgument, output);

		return await rootCommand.InvokeAsync(args);
	}

	private static IReadOnlyList<object> ExtractAndLinkJson(FileInfo lsif, DirectoryInfo source)
	{
		var linkedLSIF = new List<object>();

		var hasSourcePreamble = false;
		var hasMetaDataPreamble = false;
		string? ciStagingRoot = null;

		foreach (string line in File.ReadLines(lsif.FullName))
		{
			var node = JsonNode.Parse(line);
			if (node is null)
			{
				continue;
			}

			linkedLSIF.Add(node);

			var labelNode = node["label"];
			if (labelNode is null)
			{
				continue;
			}

			var label = labelNode.GetValue<string>();

			switch (label)
			{
				case "source":
					{
						hasSourcePreamble = true;
						break;
					}
				case "metaData":
					{
						hasMetaDataPreamble = true;
						break;
					}
				case "project":
					{
						// Need to update the document content with the corresponding file content.
						var resourceNode = node["resource"];
						if (resourceNode is null)
						{
							continue;
						}

						var resourceString = resourceNode.GetValue<string>();
						if (!Uri.TryCreate(resourceString, UriKind.Absolute, out var uri))
						{
							continue;
						}

						ciStagingRoot ??= TryGetCIStagingRoot(uri.LocalPath, source.FullName);
						if (ciStagingRoot is null)
						{
							Console.WriteWarning($"Could not link project '{uri.LocalPath}' to source directory '{source.FullName}'.");
							continue;
						}
						var relativePath = uri.LocalPath.Substring(ciStagingRoot.Length);
						if (!Uri.TryCreate(relativePath, UriKind.RelativeOrAbsolute, out var relativeUri))
						{
							continue;
						}

						node["resource"] = GetLinkedFilePath(relativeUri, source);
						Console.WriteLine($"- Linked {relativePath}");
						break;
					}
				case "document":
					{
						var contents = node["contents"];
						if (contents is not null)
						{
							continue;
						}

						// Need to update the document content with the corresponding file content.
						var uriNode = node["uri"];
						if (uriNode is null)
						{
							continue;
						}

						var uriString = uriNode.GetValue<string>();
						if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
						{
							continue;
						}

						ciStagingRoot ??= TryGetCIStagingRoot(uri.LocalPath, source.FullName);
						if (ciStagingRoot is null)
						{
							Console.WriteWarning($"Provided source directory '{source.FullName}' could not be linked to document node '{uri.LocalPath}'.");
							continue;
						}
						var relativePath = uri.LocalPath.Substring(ciStagingRoot.Length);
						if (!Uri.TryCreate(relativePath, UriKind.RelativeOrAbsolute, out var relativeUri))
						{
							continue;
						}

						node["uri"] = GetLinkedFilePath(relativeUri, source);
						Console.WriteLine($"- Linked {relativePath}");

						var readablePath = GetLocalPath(relativeUri, source);
						if (!File.Exists(readablePath))
						{
							Console.WriteWarning($"Could not link document '{relativeUri}''s content to source directory '{readablePath}'. Document content may not be available.");
							continue;
						}

						var fileBytes = File.ReadAllBytes(readablePath);
						var base64Content = Convert.ToBase64String(fileBytes);
						node["contents"] = base64Content;
						Console.WriteLine("  + Embedded document content");

						break;
					}
			}
		}

		if (!hasSourcePreamble)
		{
			if (!Uri.TryCreate(source.FullName, UriKind.Absolute, out var workspaceRoot))
			{
				throw new InvalidOperationException($"Cannot convert {source.FullName} into a Uri");
			}

			linkedLSIF.Insert(0, new SourcePreamble(workspaceRoot));
		}

		if (!hasMetaDataPreamble)
		{
			linkedLSIF.Insert(0, MetadataPreamble.Instance);
		}

		return linkedLSIF;
	}

	private static string GetLinkedFilePath(Uri relativeUri, DirectoryInfo source)
	{
		var stringifiedUri = relativeUri.ToString().TrimStart(DirectoryPathSeparators);
		var combinedPath = Path.Combine(source.FullName, stringifiedUri);
		var normalizedPath = combinedPath.Replace('\\', '/');
		if (!Uri.TryCreate(normalizedPath, UriKind.Absolute, out var linkedUri))
		{
			throw new InvalidOperationException($"Was unable to convert '{normalizedPath}' to a URI.");
		}

		return linkedUri.ToString();
	}

	private static string GetLocalPath(Uri uri, DirectoryInfo source)
	{
		var localPath = uri.OriginalString;
		if (!localPath.StartsWith(source.FullName))
		{
			localPath = localPath.TrimStart(DirectoryPathSeparators);

			// Relative path, make absolute;
			localPath = Path.Combine(source.FullName, localPath);
		}

		return localPath;
	}

	private static string? TryResolveOutputFilePath(FileInfo lsif, FileInfo? outputFile)
	{
		var outputFilePath = outputFile?.FullName;
		if (outputFilePath is null)
		{
			outputFilePath = Path.ChangeExtension(lsif.FullName, "linked.lsif");
		}

		var directory = Path.GetDirectoryName(outputFilePath);
		if (!Directory.Exists(directory))
		{
			Console.WriteError($"Could not resolve output directory '{directory}'");
			return null;
		}

		return outputFilePath;
	}

	private static async Task SerializeAsync(IReadOnlyList<object> linkedJson, string outputFilePath, CancellationToken cancellationToken)
	{
		using var outputStream = File.Create(outputFilePath);
		using var writer = new StreamWriter(outputStream);
		writer.AutoFlush = true;

		for (var i = 0; i < linkedJson.Count; i++)
		{
			var item = linkedJson[i];
			await JsonSerializer.SerializeAsync(outputStream, item, SerializerOptions, cancellationToken);

			if (i + 1 < linkedJson.Count)
			{
				// Separate each Json node by a newline
				await writer.WriteLineAsync();
			}
		}
	}

	private static string? TryGetCIStagingRoot(string ciPath, string sourceRoot)
	{
		// Goal of this method is to use the source machine root (which we know is a directory, provided via
		// command line arguments) to find what part of the LSIF path belongs to the CI.
		// Meaning, if you have:
		// 
		// ciPath: "C:/d/1/2/Foo/Bar/Baz/file.cs"
		// sourceRoot: "C:/Users/JohnDoe/Repos/Foo/Bar/"
		//
		// We can then find that the CI staging root was "C:/d/1/2/". The reason why this is important is to
		// allow us to convert LSIF document/project paths to be relative

		if (ciPath.StartsWith(sourceRoot))
		{
			// This can only really happen if we're trying to link LSIF output on the same machine that it was
			// generated from.

			return string.Empty;
		}

		var ciFileName = Path.GetFileName(ciPath);
		var localFilePath = Directory.GetFiles(sourceRoot, ciFileName, SearchOption.AllDirectories).FirstOrDefault();

		if (localFilePath is null)
		{
			// Cannot find associated file to potentially compare
			return null;
		}

		var ciPathSpan = ciPath.AsSpan();
		var start = 0;
		while (start < localFilePath.Length)
		{
			var nextSeparator = localFilePath.IndexOfAny(DirectoryPathSeparators, start);
			if (nextSeparator == -1)
			{
				break;
			}

			var pathSuffix = localFilePath.AsSpan(nextSeparator);
			var pathSuffixIndex = ciPathSpan.IndexOf(pathSuffix);
			if (pathSuffixIndex >= 0)
			{
				return ciPath.Substring(0, pathSuffixIndex);
			}

			start = nextSeparator + 1;
		}

		return null;
	}
}
