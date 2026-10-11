using System.Globalization;
using System.Text;
using System.Text.Json;

namespace W2D1;

internal static class LocalOptSummary
{
    private sealed record Metrics(string Name, double? MeanMilliseconds, long? Tokens,
        int Successful, int Errors, int Refusals, int Skipped, int Timed, int TokenSamples, int Requested);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Directory.GetParent(new DocumentChunkingService().DocumentsDirectory)!.FullName;
        var path = Path.Combine(root, "reports", "local-opt-benchmark.json");
        try
        {
            using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            // Build the entire output first: invalid structure must not produce a misleading partial table.
            Console.WriteLine(Format(document.RootElement, path));
        }
        catch (FileNotFoundException) { Console.WriteLine($"Отчёт отсутствует: {path}. Сводка не выполнена."); }
        catch (DirectoryNotFoundException) { Console.WriteLine($"Отчёт отсутствует: {path}. Сводка не выполнена."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            Console.WriteLine($"Отчёт повреждён или имеет неподдерживаемую структуру: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Не удалось прочитать отчёт: {ex.Message}");
        }
        catch (OperationCanceledException) { Console.WriteLine("Чтение сводки отменено."); }
    }

    private static string Format(JsonElement root, string path)
    {
        var configs = Required(root, "configurations", JsonValueKind.Array).EnumerateArray().ToArray();
        var runs = Required(root, "runs", JsonValueKind.Array).EnumerateArray().ToArray();
        var names = new[] { "BASELINE", "OPTIMIZED" };
        var text = new StringBuilder();
        text.AppendLine($"Локальная оптимизация — существующий отчёт: {path}");
        text.AppendLine($"Модель: {String(root, "model") ?? "нет данных"}; дата: {String(root, "startedAt") ?? "нет данных"}");
        var summaries = new List<Metrics>();
        foreach (var name in names)
        {
            if (configs.Count(c => String(c, "Name") == name) != 1)
                throw new FormatException($"Ожидается одна конфигурация {name}.");
            var group = runs.Where(r => String(r, "Configuration") == name).ToArray();
            if (group.Length == 0) throw new FormatException($"Нет результатов {name}.");
            var durations = new List<double>();
            var tokens = new List<long>();
            var success = 0; var errors = 0; var refusals = 0; var skipped = 0; var requested = 0;
            foreach (var run in group)
            {
                var status = String(run, "Status") ?? throw new FormatException("Отсутствует Status.");
                if (status == "success") success++;
                else if (status is "model_refusal" or "retrieval_refusal") refusals++;
                else if (status is "error" or "timeout" or "cancelled") errors++;
                else skipped++;
                if (run.TryGetProperty("GenerationRequested", out var generation) && generation.ValueKind == JsonValueKind.True)
                {
                    requested++;
                    // Failed/time-out requests are counted as errors, not as completed timing samples.
                    if (status is "success" or "model_refusal")
                    {
                        var duration = Numeric(run, "GenerationRequestMilliseconds");
                        if (duration is { } ms) durations.Add(ms);
                    }
                    var count = Integer(run, "GeneratedTokens");
                    if (count is null && run.TryGetProperty("OllamaResponse", out var response)
                        && response.ValueKind == JsonValueKind.Object)
                        count = Integer(response, "eval_count");
                    if (count is { } value) tokens.Add(value);
                }
            }
            summaries.Add(new Metrics(name, durations.Count == 0 ? null : durations.Average(),
                tokens.Count == 0 ? null : tokens.Sum(), success, errors, refusals, skipped,
                durations.Count, tokens.Count, requested));
        }
        text.AppendLine();
        text.AppendLine($"{"Конфигурация",-14} {"Ollama, мс",12} {"Токены, Σ",12} {"Успешно",9} {"Ошибки",8}");
        foreach (var m in summaries)
            text.AppendLine($"{m.Name,-14} {Display(m.MeanMilliseconds),12} {m.Tokens?.ToString(CultureInfo.InvariantCulture) ?? "н/д",12} {m.Successful,9} {m.Errors,8}");
        text.AppendLine("Среднее: GenerationRequestMilliseconds завершённых ответов, включая отказы модели; без поиска и подготовки.");
        foreach (var m in summaries)
            text.AppendLine($"{m.Name}: замеров времени {m.Timed}; токены доступны для {m.TokenSamples}/{m.Requested} вызовов; отказы {m.Refusals}; прочие/незавершённые {m.Skipped}.");
        var baseline = summaries[0]; var optimized = summaries[1];
        text.AppendLine();
        text.AppendLine("Изменения OPTIMIZED относительно BASELINE:");
        if (baseline.MeanMilliseconds is > 0 && optimized.MeanMilliseconds is > 0)
        {
            text.AppendLine($"Скорость (1 / среднее время): {Signed((baseline.MeanMilliseconds.Value / optimized.MeanMilliseconds.Value - 1) * 100)}.");
            text.AppendLine($"Среднее время: {Signed((optimized.MeanMilliseconds.Value / baseline.MeanMilliseconds.Value - 1) * 100)}.");
        }
        else text.AppendLine("Изменение скорости: н/д (нет положительных средних времён).");
        if (baseline.Tokens is > 0 && optimized.Tokens is { } optimizedTokens
            && baseline.TokenSamples == baseline.Requested && optimized.TokenSamples == optimized.Requested)
            text.AppendLine($"Количество токенов: {Signed(((double)optimizedTokens / baseline.Tokens.Value - 1) * 100)}.");
        else text.AppendLine("Изменение количества токенов: н/д (неполные метрики или нулевая база).");
        text.AppendLine("Проценты описывают этот отчёт; при разном составе завершённых запросов сравнение ограничено. Скорость не оценивает качество.");

        text.AppendLine();
        text.AppendLine("Настройки из отчёта:");
        foreach (var name in names)
        {
            var config = configs.Single(c => String(c, "Name") == name);
            var options = Required(config, "Options", JsonValueKind.Object);
            var prompt = String(config, "SystemPrompt") ?? throw new FormatException($"Нет промпта {name}.");
            var compact = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            text.AppendLine($"{name}: temperature={Option(options, "temperature")}; num_predict={Option(options, "num_predict")}; num_ctx={Option(options, "num_ctx")}");
            text.AppendLine($"Промпт ({prompt.Length} символов): {compact[..Math.Min(compact.Length, 180)]}{(compact.Length > 180 ? "…" : "")}");
        }
        if (String(root, "baselineOrigin") is { } origin && origin.Contains("context is increased", StringComparison.OrdinalIgnoreCase))
            text.AppendLine("В benchmark контекст BASELINE увеличен относительно исходных настроек Ollama; исходная конфигурация local-rag сохранена.");

        text.AppendLine();
        var quantization = new HashSet<string>();
        if (root.TryGetProperty("modelDetails", out var modelDetails)) AddQuantization(modelDetails, quantization);
        foreach (var run in runs)
            foreach (var key in new[] { "ResourcesBefore", "ResourcesAfter" })
                if (run.TryGetProperty(key, out var snapshot)) AddQuantization(snapshot, quantization);
        text.AppendLine($"Квантование из отчёта: {(quantization.Count == 0 ? "н/д (Q4_K_M не подтверждено отчётом)" : string.Join(", ", quantization.OrderBy(q => q)))}");
        foreach (var name in names)
        {
            var allocations = new List<long>();
            foreach (var run in runs.Where(r => String(r, "Configuration") == name))
                foreach (var key in new[] { "ResourcesBefore", "ResourcesAfter" })
                    if (run.TryGetProperty(key, out var snapshot) && snapshot.ValueKind == JsonValueKind.Object
                        && Integer(snapshot, "size_vram") is { } bytes) allocations.Add(bytes);
            text.AppendLine(allocations.Count == 0
                ? $"{name}: VRAM — н/д."
                : $"{name}: выделенная VRAM по size_vram: {allocations.Min():N0}–{allocations.Max():N0} байт ({allocations.Min() / 1048576.0:F1}–{allocations.Max() / 1048576.0:F1} MiB), снимков {allocations.Count}.");
        }
        text.AppendLine("Это выделенная память по снимкам Ollama, не пиковое потребление VRAM. Пиковое потребление не измерено.");
        text.AppendLine();
        text.AppendLine("================================================================");
        text.AppendLine("СРАВНЕНИЕ КАЧЕСТВА ОТВЕТОВ");
        text.AppendLine("Полные ответы из отчёта. Сравните качество самостоятельно.");
        text.AppendLine("Время генерации — метрика Ollama; время запроса включает весь HTTP-вызов.");
        var questions = new[]
        {
            "What is encapsulation in C#?",
            "What is inheritance in C#?",
            "What is the difference between a class and a struct in C#?",
            "What is async/await in C#?"
        };
        for (var questionIndex = 0; questionIndex < questions.Length; questionIndex++)
        {
            text.AppendLine();
            text.AppendLine("================================================================");
            text.AppendLine($"ВОПРОС {questionIndex + 1}/4: {questions[questionIndex]}");
            foreach (var name in names)
            {
                text.AppendLine();
                text.AppendLine($"-------------------- {name} --------------------");
                var answers = runs.Where(r => String(r, "Configuration") == name
                    && String(r, "Question") == questions[questionIndex]).ToArray();
                if (answers.Length == 0)
                {
                    text.AppendLine("Ответ отсутствует");
                    text.AppendLine("Причина: в отчёте нет результата для этого вопроса и конфигурации.");
                    text.AppendLine("Время генерации: н/д | Выходные токены: н/д");
                }
                for (var answerIndex = 0; answerIndex < answers.Length; answerIndex++)
                {
                    var answer = answers[answerIndex];
                    if (answers.Length > 1) text.AppendLine($"Сохранённый ответ {answerIndex + 1}/{answers.Length}");
                    var duration = Integer(answer, "GenerationDurationNanoseconds");
                    var count = Integer(answer, "GeneratedTokens");
                    if (answer.TryGetProperty("OllamaResponse", out var response)
                        && response.ValueKind == JsonValueKind.Object)
                    {
                        duration ??= Integer(response, "eval_duration");
                        count ??= Integer(response, "eval_count");
                    }
                    text.AppendLine($"Время генерации: {Display(duration is { } ns ? ns / 1000000.0 : null)} мс | " +
                        $"Выходные токены: {count?.ToString(CultureInfo.InvariantCulture) ?? "н/д"}");
                    var requestTime = Numeric(answer, "GenerationRequestMilliseconds");
                    text.AppendLine($"Время запроса к Ollama: {Display(requestTime)} мс");
                    text.AppendLine();
                    var content = String(answer, "Answer");
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        text.AppendLine("Ответ отсутствует");
                        var error = String(answer, "Error");
                        var refusal = String(answer, "Refusal");
                        if (!string.IsNullOrWhiteSpace(error)) text.AppendLine($"Причина: {error}");
                        if (!string.IsNullOrWhiteSpace(refusal)) text.AppendLine($"Причина отказа: {refusal}");
                        if (string.IsNullOrWhiteSpace(error) && string.IsNullOrWhiteSpace(refusal))
                            text.AppendLine($"Причина не сохранена. Статус: {String(answer, "Status") ?? "н/д"}.");
                    }
                    else text.AppendLine(content);
                }
            }
        }
        text.AppendLine("================================================================");
        return text.ToString();
    }

    private static JsonElement Required(JsonElement value, string key, JsonValueKind kind) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == kind
            ? property : throw new FormatException($"Отсутствует или некорректно поле {key}.");

    private static string? String(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Ожидается JSON-объект.");
        if (!value.TryGetProperty(key, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : throw new FormatException($"Некорректная строка {key}.");
    }

    private static double? Numeric(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
            throw new FormatException($"Некорректная метрика {key}.");
        return number;
    }

    private static long? Integer(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number) || number < 0)
            throw new FormatException($"Некорректная метрика {key}.");
        return number;
    }

    private static string Option(JsonElement options, string key) => Numeric(options, key) is { } number
        ? number.ToString("G6", CultureInfo.InvariantCulture) : "по умолчанию Ollama/модели";
    private static string Display(double? number) => number?.ToString("F1", CultureInfo.InvariantCulture) ?? "н/д";
    private static string Signed(double number) => number.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
    private static void AddQuantization(JsonElement snapshot, HashSet<string> levels)
    {
        if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Object && String(details, "quantization_level") is { } level)
            levels.Add(level);
    }
}
