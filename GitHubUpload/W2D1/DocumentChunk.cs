namespace W2D1;

public sealed record DocumentChunk(
    string Text,
    string Source,
    string Title,
    string Section,
    string ChunkId,
    string Strategy)
{
    public float[] Embedding { get; init; } = Array.Empty<float>();
}
