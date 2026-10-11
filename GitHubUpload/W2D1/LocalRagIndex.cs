namespace W2D1;

// Local indexes carry embedding provenance; cloud indexes retain their array format.
internal sealed record LocalRagIndex(string EmbeddingModel, int EmbeddingDimensions,
    DocumentChunk[] Chunks);
