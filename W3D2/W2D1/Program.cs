using System.Text;
using W2D1;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
// В Windows ReadFile при CP_UTF8 может заменять кириллицу на NUL.
// Для консоли читаем Unicode напрямую; перенаправленный stdin остаётся UTF-8.
if (OperatingSystem.IsWindows() && !Console.IsInputRedirected)
    Console.SetIn(new WindowsConsoleReader());
using var agent = new LlmAgent();
agent.Progress += Console.WriteLine;
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
    Console.WriteLine($"Агент: {await agent.AskAsync(request, cancellation.Token)}");
    if (agent.Statistics is not null)
        Console.WriteLine($"\n{agent.Statistics}");
    Console.WriteLine();
}
