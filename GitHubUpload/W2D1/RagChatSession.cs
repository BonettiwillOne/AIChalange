using System.Text.Json;

namespace W2D1;

// One isolated, in-memory conversation per entry into /rag-chat.
internal sealed class RagChatSession
{
    private const string Unknown = "Не знаю: в локальной базе документов недостаточно релевантной информации.";
    private const string Help = "/state; /set-goal <текст>; /add-constraint <текст>; /add-term <текст>; /history; /exit";
    private readonly LlmAgent _agent;
    private readonly SemanticSearchService _search;
    private readonly List<ChatTurn> _history = new();
    private TaskState _state = new();
    private sealed record ChatTurn(string User, string Assistant);

    public RagChatSession(LlmAgent agent, SemanticSearchService search)
    {
        _agent = agent;
        _search = search;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("RAG-чат: задавайте вопросы по локальным документам.\n" + Help);
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("RAG Вы: ");
            var input = Console.ReadLine()?.Trim();
            if (input is null || cancellationToken.IsCancellationRequested) return;
            if (input.Length == 0) continue;
            if (input.StartsWith('/'))
            {
                var parts = input.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLowerInvariant();
                var value = parts.Length == 2 ? parts[1].Trim() : "";
                if (command == "/exit" && value.Length == 0) return;
                if (command == "/state" && value.Length == 0)
                    Console.WriteLine($"Goal: {_state.Goal}\nConstraints:\n{string.Join('\n', _state.Constraints)}\nTerms:\n{string.Join('\n', _state.Terms)}");
                else if (command == "/history" && value.Length == 0)
                {
                    if (_history.Count == 0) Console.WriteLine("История пуста.");
                    for (var i = 0; i < _history.Count; i++)
                        Console.WriteLine($"{i + 1}. Вы: {Preview(_history[i].User)}\n   Ассистент: {Preview(_history[i].Assistant)}");
                }
                else if (command is "/set-goal" or "/add-constraint" or "/add-term")
                {
                    if (value.Length == 0) Console.WriteLine($"Укажите текст: {command} <текст>.");
                    else
                    {
                        _state = command switch
                        {
                            "/set-goal" => _state with { TaskDescription = value },
                            "/add-constraint" => _state with { Constraints = _state.Constraints.Append(value).ToArray() },
                            _ => _state with { Terms = _state.Terms.Append(value).ToArray() }
                        };
                        Console.WriteLine("Task State обновлён.");
                    }
                }
                else Console.WriteLine("Команды RAG-чата: " + Help);
                continue;
            }

            try
            {
                var conversation = JsonSerializer.Serialize(new
                {
                    CurrentQuestion = input,
                    TaskState = new { _state.Goal, _state.Constraints, _state.Terms },
                    History = _history
                });
                var query = await _agent.RewriteChatQueryAsync(conversation, cancellationToken);
                var initial = await _search.SearchAsync(query, SemanticSearchService.CandidateCount, cancellationToken);
                var candidates = initial.Where(result => result.Similarity >= SemanticSearchService.SimilarityThreshold)
                    .Take(SemanticSearchService.FilteredResultLimit).ToArray();
                string answer;
                if (candidates.Length == 0)
                {
                    // No answer generation call on an empty retrieval result.
                    answer = Unknown + "\nПожалуйста, уточните вопрос.";
                }
                else
                {
                    var ranking = await _agent.RerankAsync(conversation, candidates, cancellationToken);
                    if (ranking.FallbackReason is not null)
                        Console.WriteLine($"Reranker: {ranking.FallbackReason} Использован порядок cosine similarity.");
                    var sources = ranking.CandidateNumbers.Take(LlmAgent.RerankedResultLimit)
                        .Select(number => candidates[number - 1]).ToArray();
                    answer = await _agent.AnswerChatAsync(conversation, sources, cancellationToken);
                    Console.WriteLine("Ассистент: " + answer);
                    for (var i = 0; i < sources.Length; i++)
                        Console.WriteLine($"[{i + 1}] Source: {sources[i].Chunk.Source}\nSection: {sources[i].Chunk.Section}\nChunkId: {sources[i].Chunk.ChunkId}");
                    _history.Add(new ChatTurn(input, answer));
                    continue;
                }
                Console.WriteLine("Ассистент: " + answer);
                _history.Add(new ChatTurn(input, answer));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A failed turn is retained without pretending it was an evidence-based answer.
                var error = $"Не удалось получить ответ RAG: {ex.Message}";
                Console.WriteLine(error);
                _history.Add(new ChatTurn(input, error));
            }
        }
    }

    private static string Preview(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 180 ? singleLine : singleLine[..180] + "…";
    }
}
