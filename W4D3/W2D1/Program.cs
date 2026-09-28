using System.Text;
using ModelContextProtocol.Protocol;
using W2D1;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
// В Windows ReadFile при CP_UTF8 может заменять кириллицу на NUL.
// Для консоли читаем Unicode напрямую; перенаправленный stdin остаётся UTF-8.
if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
    Console.SetIn(new WindowsConsoleReader());
using var agent = new LlmAgent();
var mcpClient = new McpClientService();
agent.Progress += Console.WriteLine;
Console.WriteLine("Инварианты: /invariants; /invariant add <category> | <rule> | <reason>.");
Console.WriteLine("Управление: /invariant remove <id>; /invariant disable <id>; /invariant enable <id>.");
Console.WriteLine("Инварианты обязательны; /reset, /task clear, очистка памяти и переключение профиля их сохраняют.");
Console.WriteLine("Задача: /task; /task start <описание>; /task step <текст>; /task expect <текст>.");
Console.WriteLine("Подтверждения: /task approve-plan; /task complete-execution; /task validation pass; /task validation fail.");
Console.WriteLine("Этапы: /task next; /task revise. Пауза: /task pause; /task resume. Удаление: /task clear.");
Console.WriteLine("/task start заменяет текущую задачу. /reset и очистка памяти сохраняют Task State.");
Console.WriteLine("Профиль: /profile; /profile set key=value; /profile remove key; /profile clear.");
Console.WriteLine("Профили: /profiles; /profile create имя; /profile switch имя; /profile delete имя.");
Console.WriteLine("Профили и активный выбор сохраняются между запусками; /reset и /clear all их не сбрасывают.");
Console.WriteLine("Память: /remember working key=value; /remember long key=value.");
Console.WriteLine("Просмотр: /memory short|working|long|all. Очистка: /clear short|working|long|all.");
Console.WriteLine("/reset сохраняет долговременную память. Summary и автоизвлечение facts отключены.");
Console.WriteLine(agent.HistoryStatus);
Console.WriteLine(agent.StrategyStatus);
Console.WriteLine("Стратегии: /strategy sliding|facts|branching. Память: /facts.");
Console.WriteLine("Окно Sliding: /sliding N (включая новый запрос); /sliding — текущий размер.");
Console.WriteLine("Ветки: /checkpoint имя; /branch ветка [checkpoint]; /switch ветка; /branches.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};
Console.WriteLine("Введите запрос на русском языке. Новый диалог: /reset. Выход: /выход, /exit или Ctrl+C.");
Console.WriteLine("Учебный тест переполнения контекста: /overflow-test.");
Console.WriteLine("Инструменты MCP-сервера: /mcp-tools.");
Console.WriteLine("Курс валют через MCP: /currency <код>, например /currency USD.");
Console.WriteLine("Сводка курсов валют через MCP: /currency-summary.");
while (!cancellation.IsCancellationRequested)
{
    Console.Write("Вы: ");
    var request = Console.ReadLine();
    if (request is null || request.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase)
                        || request.Trim().Equals("/выход", StringComparison.OrdinalIgnoreCase)
                        || cancellation.IsCancellationRequested)
        break;
    if (string.IsNullOrWhiteSpace(request))
        continue;
    var commandParts = request.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (commandParts[0].Equals("/currency-summary", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 1)
        {
            Console.WriteLine("Команда не требует аргументов: /currency-summary.");
            continue;
        }

        try
        {
            Console.WriteLine($"Получение сводки курсов через MCP: {McpClientService.ServerUrl}...");
            var (toolName, result) = await mcpClient.GetCurrencySummaryAsync(cancellation.Token);
            Console.WriteLine($"MCP-инструмент: {toolName}");
            if (result.IsError == true)
                Console.WriteLine("MCP-инструмент вернул ошибку:");

            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Не удалось получить сводку: тайм-аут MCP (30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить сводку через MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (commandParts[0].Equals("/currency", StringComparison.OrdinalIgnoreCase))
    {
        if (commandParts.Length != 2 || commandParts[1].Length != 3 ||
            commandParts[1].Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))))
        {
            Console.WriteLine("Укажите код валюты из трёх латинских букв: /currency USD или /currency EUR.");
            continue;
        }

        try
        {
            var currency = commandParts[1].ToUpperInvariant();
            Console.WriteLine($"Получение курса {currency} через MCP: {McpClientService.ServerUrl}...");
            var (toolName, result) = await mcpClient.GetCurrencyAsync(currency, cancellation.Token);
            Console.WriteLine($"MCP-инструмент: {toolName}");
            if (result.IsError == true)
                Console.WriteLine("MCP-инструмент вернул ошибку:");

            var textBlocks = result.Content.OfType<TextContentBlock>().ToArray();
            foreach (var block in textBlocks)
                Console.WriteLine(block.Text);
            if (textBlocks.Length == 0)
                Console.WriteLine(result.StructuredContent?.GetRawText() ?? "MCP-инструмент не вернул текстовый результат или структурированные данные.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Не удалось получить курс: тайм-аут MCP (30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить курс через MCP: {ex.Message}");
        }
        Console.WriteLine();
        continue;
    }
    if (request.Trim().Equals("/mcp-tools", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            Console.WriteLine($"Получение инструментов MCP: {McpClientService.ServerUrl}...");
            var tools = await mcpClient.ListToolsAsync(cancellation.Token);
            Console.WriteLine($"Доступные инструменты MCP: {tools.Count}");
            foreach (var tool in tools)
            {
                Console.WriteLine($"Имя: {tool.Name}");
                Console.WriteLine($"Описание: {(string.IsNullOrWhiteSpace(tool.Description) ? "не указано сервером" : tool.Description)}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(cancellation.IsCancellationRequested
                ? "Запрос MCP отменён."
                : "Сервер MCP не ответил вовремя (тайм-аут 30 секунд). Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось подключиться к MCP-серверу или получить список инструментов: {ex.Message}");
            Console.WriteLine("Проверьте доступность сервера и адрес MCP endpoint.");
        }
        Console.WriteLine();
        continue;
    }
    Console.WriteLine($"Агент: {await agent.AskAsync(request, cancellation.Token)}");
    if (agent.Statistics is not null)
        Console.WriteLine($"\n{agent.Statistics}");
    Console.WriteLine();
}
