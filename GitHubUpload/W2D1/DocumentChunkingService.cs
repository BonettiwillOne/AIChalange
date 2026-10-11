using System.Text;
using System.Text.RegularExpressions;

namespace W2D1;

public sealed class DocumentChunkingService
{
    private const int ChunkSize = 1000;
    public string DocumentsDirectory => FindDocumentsDirectory();

    public async Task<(int DocumentCount, IReadOnlyList<DocumentChunk> FixedChunks,
        IReadOnlyList<DocumentChunk> StructureChunks)> ChunkDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var directory = DocumentsDirectory;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var fixedChunks = new List<DocumentChunk>();
        var structureChunks = new List<DocumentChunk>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
            var headings = FindHeadings(text);
            var title = headings.FirstOrDefault(heading => heading.Level == 1)?.Name
                ?? headings.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(file);
            var source = Path.GetRelativePath(directory, file).Replace('\\', '/');

            for (var start = 0; start < text.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(ChunkSize, text.Length - start);
                // Keep UTF-16 surrogate pairs together without dropping any characters.
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])
                    && char.IsLowSurrogate(text[start + length]))
                    length--;
                var section = headings.LastOrDefault(heading => heading.Offset <= start)?.Name
                    ?? "Введение";
                fixedChunks.Add(CreateChunk(text.Substring(start, length), source, title, section,
                    "fixed", start));
                start += length;
            }

            // Every heading starts a section. Even large sections stay whole.
            var sectionStart = 0;
            var sectionName = "Введение";
            foreach (var heading in headings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (heading.Offset > sectionStart)
                    structureChunks.Add(CreateChunk(text[sectionStart..heading.Offset], source,
                        title, sectionName, "structure", sectionStart));
                sectionStart = heading.Offset;
                sectionName = heading.Name;
            }
            if (sectionStart < text.Length)
                structureChunks.Add(CreateChunk(text[sectionStart..], source, title,
                    sectionName, "structure", sectionStart));
        }

        return (files.Length, fixedChunks, structureChunks);
    }

    private static DocumentChunk CreateChunk(string text, string source, string title,
        string section, string strategy, int offset)
        => new(text, source, title, section, $"{source}:{strategy}:{offset}", strategy);

    private static string FindDocumentsDirectory()
    {
        foreach (var origin in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "documents");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        throw new DirectoryNotFoundException(
            "Папка documents не найдена рядом с проектом или solution. " +
            $"Каталог сборки: {AppContext.BaseDirectory}");
    }

    private sealed record Heading(int Offset, int Level, string Name);

    private static List<Heading> FindHeadings(string text)
    {
        var headings = new List<Heading>();
        char fenceCharacter = '\0';
        var fenceLength = 0;
        var frontMatter = false;
        foreach (Match line in Regex.Matches(text, @"[^\r\n]+|\r?\n"))
        {
            var value = line.Value;
            if (line.Index == 0 && value.Trim() == "---")
            {
                frontMatter = true;
                continue;
            }
            if (frontMatter)
            {
                if (value.Trim() is "---" or "...")
                    frontMatter = false;
                continue;
            }

            var fence = Regex.Match(value, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (fenceCharacter != '\0')
            {
                if (fence.Success && fence.Groups[1].Value[0] == fenceCharacter
                    && fence.Groups[1].Length >= fenceLength
                    && string.IsNullOrWhiteSpace(fence.Groups[2].Value))
                    fenceCharacter = '\0';
                continue;
            }
            if (fence.Success)
            {
                fenceCharacter = fence.Groups[1].Value[0];
                fenceLength = fence.Groups[1].Length;
                continue;
            }

            var heading = Regex.Match(value, @"^ {0,3}(#{1,6})(?:[ \t]+(.*)|[ \t]*)$");
            if (heading.Success)
            {
                var name = Regex.Replace(heading.Groups[2].Value, @"[ \t]+#+[ \t]*$", "").Trim();
                headings.Add(new Heading(line.Index, heading.Groups[1].Length,
                    name.Length > 0 ? name : "Без названия"));
            }
        }
        return headings;
    }
}
