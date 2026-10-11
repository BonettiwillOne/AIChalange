using System.Text.Json;

namespace W2D1;

public sealed record IndexBuildResult(int DocumentCount, int FixedChunkCount,
    int StructureChunkCount, int IndexedChunkCount, int EmbeddingDimensions,
    string FilePath, long FileSizeBytes);

public sealed class LocalIndexService
{
    private readonly DocumentChunkingService _chunkingService;
    private readonly Func<string, CancellationToken, Task<float[]>> _generateEmbedding;
    private readonly string _indexFileName;
    private readonly int _maxChunkCharacters;
    private readonly string? _embeddingModel;

    public LocalIndexService(DocumentChunkingService chunkingService,
        DocumentEmbeddingService embeddingService)
        : this(chunkingService, embeddingService.GenerateEmbeddingAsync, "rag-index.json") { }

    internal LocalIndexService(DocumentChunkingService chunkingService,
        Func<string, CancellationToken, Task<float[]>> generateEmbedding, string indexFileName,
        int maxChunkCharacters = 0, string? embeddingModel = null)
    {
        _chunkingService = chunkingService;
        _generateEmbedding = generateEmbedding;
        _indexFileName = indexFileName;
        if (maxChunkCharacters != 0 && maxChunkCharacters < 2)
            throw new ArgumentOutOfRangeException(nameof(maxChunkCharacters));
        _maxChunkCharacters = maxChunkCharacters;
        _embeddingModel = embeddingModel;
    }

    public async Task<IndexBuildResult> BuildIndexAsync(Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var documentsDirectory = _chunkingService.DocumentsDirectory;
        var result = await _chunkingService.ChunkDocumentsAsync(cancellationToken);
        var originalChunks = result.FixedChunks.Concat(result.StructureChunks);
        var chunks = (_maxChunkCharacters > 0
            ? originalChunks.SelectMany(chunk => SplitChunk(chunk, _maxChunkCharacters))
            : originalChunks).ToList();
        if (chunks.Count == 0)
            throw new InvalidOperationException("Нет чанков для индексации. Проверьте папку documents.");

        var dimensions = 0;
        progress?.Invoke($"Индексация: 0/{chunks.Count}");
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] embedding;
            try
            {
                embedding = await _generateEmbedding(chunks[i].Text, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (OllamaContextLengthException) when (_maxChunkCharacters > 0)
            {
                // Retry only the rejected fragment, keeping all already computed embeddings.
                // A Unicode scalar (one character or a surrogate pair) cannot be split further.
                var chunk = chunks[i];
                var parts = SplitChunk(chunk, Math.Max(1, chunk.Text.Length / 2)).ToArray();
                if (parts.Length < 2)
                    throw new InvalidOperationException(
                        $"Ollama отклоняет минимальный фрагмент {chunk.ChunkId}. Проверьте контекст модели. Текст не пропущен; существующий индекс сохранён.");
                chunks.RemoveAt(i);
                chunks.InsertRange(i, parts);
                progress?.Invoke($"Лимит контекста: {chunk.ChunkId} разбит на {parts.Length} части. Индексация: {i}/{chunks.Count}; повтор фрагмента.");
                i--;
                continue;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Ошибка для чанка {chunks[i].ChunkId} ({i + 1}/{chunks.Count}): {ex.Message}", ex);
            }
            if (embedding.Length == 0 || embedding.Any(value => !float.IsFinite(value)))
                throw new InvalidOperationException($"Некорректный embedding для чанка {chunks[i].ChunkId}.");
            if (i == 0)
                dimensions = embedding.Length;
            else if (embedding.Length != dimensions)
                throw new InvalidOperationException($"Размерность embedding отличается для чанка {chunks[i].ChunkId}.");

            chunks[i] = chunks[i] with { Embedding = embedding };
            progress?.Invoke($"Индексация: {i + 1}/{chunks.Count}");
        }

        var root = Directory.GetParent(documentsDirectory)
            ?? throw new DirectoryNotFoundException("Не удалось определить родительскую папку documents.");
        var filePath = Path.Combine(root.FullName, "index", _indexFileName);
        var size = await SaveIndexAsync(chunks.ToArray(), filePath, dimensions, cancellationToken);
        return new IndexBuildResult(result.DocumentCount, result.FixedChunks.Count,
            result.StructureChunks.Count, chunks.Count, dimensions, filePath, size);
    }

    private static IEnumerable<DocumentChunk> SplitChunk(DocumentChunk chunk, int maxCharacters)
    {
        if (chunk.Text.Length <= maxCharacters)
        {
            yield return chunk;
            yield break;
        }
        for (var offset = 0; offset < chunk.Text.Length;)
        {
            var length = Math.Min(maxCharacters, chunk.Text.Length - offset);
            if (offset + length < chunk.Text.Length
                && char.IsHighSurrogate(chunk.Text[offset + length - 1])
                && char.IsLowSurrogate(chunk.Text[offset + length]))
                length--;
            if (length == 0)
                length = 2; // A surrogate pair is the smallest indivisible Unicode scalar.
            // Exact adjacent substrings preserve whitespace and every UTF-16 character.
            // Nested suffixes remain unique when a part must be split again after rejection.
            yield return chunk with
            {
                Text = chunk.Text.Substring(offset, length),
                ChunkId = $"{chunk.ChunkId}:part:{offset}",
                Embedding = Array.Empty<float>()
            };
            offset += length;
        }
    }

    private async Task<long> SaveIndexAsync(DocumentChunk[] chunks, string filePath,
        int dimensions, CancellationToken cancellationToken)
    {
        // Serialize the complete index before touching the output directory or existing index.
        object index = _embeddingModel is null
            ? chunks : new LocalRagIndex(_embeddingModel, dimensions, chunks);
        var json = JsonSerializer.SerializeToUtf8Bytes(index,
            new JsonSerializerOptions { WriteIndented = true });
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".rag-index-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(json.AsMemory(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Both files are on the same filesystem. Publish only after writing and closing the temp file.
            if (File.Exists(filePath))
                File.Replace(temporaryPath, filePath, null);
            else
                File.Move(temporaryPath, filePath);
            return json.LongLength;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
