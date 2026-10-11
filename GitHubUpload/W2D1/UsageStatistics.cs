using System.Globalization;
using System.Text.Json;

namespace W2D1;

internal sealed class UsageStatistics
{
    // USD за 1 млн токенов, стандартный тариф gpt-4.1-mini, проверено 2026-09-13.
    // https://developers.openai.com/api/docs/models/gpt-4.1-mini
    private const decimal InputRate = 0.40m;
    private const decimal CachedInputRate = 0.10m;
    private const decimal OutputRate = 1.60m;

    public long TotalTokens { get; set; }
    public decimal CostUsd { get; set; }
    public bool IsPartial { get; set; }

    public string Record(JsonElement response, string request)
    {
        long? input = null, output = null, total = null, cached = null;
        if (response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = ReadCount(usage, "input_tokens");
            output = ReadCount(usage, "output_tokens");
            total = ReadCount(usage, "total_tokens");
            if (usage.TryGetProperty("input_tokens_details", out var details))
                cached = ReadCount(details, "cached_tokens");
        }

        decimal? inputCost = null, outputCost = null, cost = null;
        if (input.HasValue && output.HasValue)
        {
            var cachedCount = Math.Min(cached ?? 0, input.Value);
            inputCost = ((input.Value - cachedCount) * InputRate + cachedCount * CachedInputRate) / 1_000_000m;
            outputCost = output.Value * OutputRate / 1_000_000m;
            cost = inputCost + outputCost;
            CostUsd += cost.Value;
        }
        if (total.HasValue) TotalTokens += total.Value;
        if (!total.HasValue || !cost.HasValue) IsPartial = true;

        // Грубая эвристика, а не токенизатор: русские символы / 2, остальные / 4.
        // Оценка не участвует в расчёте расходов и не ограничивает историю.
        double estimate = 0;
        foreach (var rune in request.EnumerateRunes())
            estimate += rune.Value is >= 0x0400 and <= 0x052f ? 0.5 : 0.25;
        var partial = IsPartial ? " (неполные данные: только известный usage)" : "";
        return "Статистика:\n" +
               $"Текущий запрос: ≈{Math.Ceiling(estimate)} токенов (грубая оценка текста)\n" +
               $"История / input: {Count(input)} (вся история + текущий запрос + инструкции)\n" +
               $"Из input закэшировано: {Count(cached)}\n" +
               $"Ответ / output: {Count(output)}\n" +
               $"Всего в этом API-вызове: {Count(total)}\n" +
               $"Суммарно за диалог: {TotalTokens} токенов{partial}\n" +
               $"Стоимость текущего вызова: {Money(cost)} (input: {Money(inputCost)}, output: {Money(outputCost)})\n" +
               $"Стоимость диалога: {Money(CostUsd)}{partial}\n" +
               "Тариф за 1 млн токенов (USD): input 0.40; cached input 0.10; output 1.60." +
               (input.HasValue && !cached.HasValue ? "\nКэш не указан API: стоимость input оценена по полному тарифу." : "") +
               (!total.HasValue ? "\nAPI не предоставил usage: расход этого вызова неизвестен, нулём не считается." : "");
    }

    private static long? ReadCount(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var number)
        && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var count) && count >= 0 ? count : null;

    private static string Count(long? value) => value.HasValue ? $"{value.Value} токенов" : "нет данных API";
    private static string Money(decimal? value) => value.HasValue
        ? "≈" + value.Value.ToString("F8", CultureInfo.InvariantCulture) + " USD" : "нет данных API";
}
