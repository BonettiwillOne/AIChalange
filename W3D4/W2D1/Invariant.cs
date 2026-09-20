namespace W2D1;

// Отдельное обязательное ограничение; Category допускает категории проекта.
internal sealed record Invariant(string Id, string Category, string Rule, string Reason, bool Enabled);
