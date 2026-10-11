using System.Text.Json;

namespace W2D1;

public sealed record SemanticSearchResult(DocumentChunk Chunk, double Similarity);

public sealed class SemanticSearchService
{
    public const double SimilarityThreshold = 0.50;
    public const int CandidateCount = 10;
    public const int FilteredResultLimit = 5;
    private readonly int _embeddingDimensions;
    private readonly Func<string, CancellationToken, Task<float[]>> _generateEmbedding;
    private readonly string _indexFileName;
    private readonly string _missingIndexInstruction;
    private readonly string? _embeddingModel;

    public SemanticSearchService(DocumentEmbeddingService embeddingService)
        : this(embeddingService.GenerateEmbeddingAsync, "rag-index.json", 1536,
            "Используйте готовый индекс.") { }

    internal SemanticSearchService(Func<string, CancellationToken, Task<float[]>> generateEmbedding,
        string indexFileName, int embeddingDimensions, string missingIndexInstruction,
        string? embeddingModel = null)
    {
        _generateEmbedding = generateEmbedding;
        _indexFileName = indexFileName;
        _embeddingDimensions = embeddingDimensions;
        _missingIndexInstruction = missingIndexInstruction;
        _embeddingModel = embeddingModel;
    }

    public Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string question,
        CancellationToken cancellationToken = default) => SearchAsync(question, 5, cancellationToken);

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string question, int topCount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Укажите вопрос для семантического поиска.", nameof(question));

        if (topCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(topCount));

        var filePath = ResolveIndexPath();
        using var stream = File.OpenRead(filePath);
        DocumentChunk[] chunks;
        try
        {
            if (_embeddingModel is null)
            {
                chunks = await JsonSerializer.DeserializeAsync<DocumentChunk[]>(stream,
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Индекс не содержит массива чанков.");
            }
            else
            {
                var index = await JsonSerializer.DeserializeAsync<LocalRagIndex>(stream,
                    cancellationToken: cancellationToken);
                if (index is null
                    || !string.Equals(index.EmbeddingModel, _embeddingModel, StringComparison.Ordinal)
                    || index.EmbeddingDimensions != _embeddingDimensions)
                    throw new InvalidOperationException(
                        $"Индекс несовместим: ожидается модель {_embeddingModel}, размерность {_embeddingDimensions}. {_missingIndexInstruction}");
                chunks = index.Chunks ?? throw new InvalidOperationException(
                    $"Индекс не содержит чанков. {_missingIndexInstruction}");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Некорректный JSON индекса: {filePath}." +
                (_embeddingModel is null ? "" : $" {_missingIndexInstruction}"), ex);
        }

        if (chunks.Length == 0)
            throw new InvalidOperationException("Индекс не содержит чанков.");

        var norms = new double[chunks.Length];
        for (var i = 0; i < chunks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            if (chunk is null || chunk.Text is null)
                throw new InvalidOperationException($"Некорректный чанк в индексе: позиция {i + 1}.");
            norms[i] = VectorNorm(chunk.Embedding, $"чанка {chunk.ChunkId}");
        }

        // Only the question needs a new embedding; document vectors come from the existing file.
        var queryEmbedding = await _generateEmbedding(question.Trim(), cancellationToken);
        var queryNorm = VectorNorm(queryEmbedding, "вопроса");
        var results = new SemanticSearchResult[chunks.Length];
        for (var i = 0; i < chunks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double dotProduct = 0;
            for (var dimension = 0; dimension < queryEmbedding.Length; dimension++)
                dotProduct += (double)queryEmbedding[dimension] * chunks[i].Embedding[dimension];
            var similarity = Math.Clamp(dotProduct / (queryNorm * norms[i]), -1.0, 1.0);
            results[i] = new SemanticSearchResult(chunks[i], similarity);
        }

        return results.OrderByDescending(result => result.Similarity).Take(topCount).ToArray();
    }

    private double VectorNorm(float[]? embedding, string description)
    {
        if (embedding is null || embedding.Length != _embeddingDimensions)
            throw new InvalidOperationException($"Embedding {description} должен иметь размерность {_embeddingDimensions}." +
                (_embeddingModel is null ? "" : $" {_missingIndexInstruction}"));
        double sum = 0;
        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
                throw new InvalidOperationException($"Embedding {description} содержит некорректное число.");
            sum += (double)value * value;
        }
        if (sum == 0)
            throw new InvalidOperationException($"Embedding {description} является нулевым вектором.");
        return Math.Sqrt(sum);
    }

    private string ResolveIndexPath()
    {
        // Rider can use the project or output directory as its working directory.
        // Start with the executable location to prefer this copy of the project.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "index", _indexFileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException(
            $"Файл index/{_indexFileName} не найден рядом с проектом или solution. {_missingIndexInstruction}");
    }
}
