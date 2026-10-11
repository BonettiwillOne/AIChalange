using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var apiKey = Environment.GetEnvironmentVariable("PRIVATE_LLM_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Задайте переменную окружения PRIVATE_LLM_API_KEY.");
var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

var builder = WebApplication.CreateSlimBuilder(args);
// Explicit Listen prevents URL environment variables from exposing the service.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 8080);
    options.Limits.MaxRequestBodySize = 16 * 1024;
});
// Do not log request headers, bodies, model responses or exception details.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("chat", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Лимит: 5 запросов в минуту. Повторите позже." }, token);
    };
});

var app = builder.Build();
using var generation = new SemaphoreSlim(1, 1);
using var ollama = new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = 1
})
{
    Timeout = TimeSpan.FromSeconds(180),
    MaxResponseContentBufferSize = 1024 * 1024
};

// Sanitize errors in every environment, including Development.
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (BadHttpRequestException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = "Некорректный запрос или слишком большое тело запроса." });
        }
    }
    catch (Exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Внутренняя ошибка сервиса." });
        }
    }
});
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<PrivateChatEndpoint>() is not null)
    {
        var supplied = context.Request.Headers["X-API-Key"];
        if (supplied.Count != 1 || string.IsNullOrEmpty(supplied[0]) ||
            !CryptographicOperations.FixedTimeEquals(keyHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]!))))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Неверный или отсутствующий API-ключ." });
            return;
        }
    }
    await next(context);
});
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/chat", async (HttpContext context) =>
{
    if (!context.Request.HasJsonContentType())
        return Results.Json(new { error = "Используйте Content-Type: application/json." }, statusCode: 415);

    ChatRequest? request;
    try { request = await context.Request.ReadFromJsonAsync<ChatRequest>(context.RequestAborted); }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Некорректный JSON. Ожидается строковое поле message." });
    }
    if (string.IsNullOrWhiteSpace(request?.Message))
        return Results.BadRequest(new { error = "Поле message должно содержать непустую строку." });
    if (request.Message.Length > 1000)
        return Results.BadRequest(new { error = "Сообщение не должно превышать 1000 символов." });

    if (!generation.Wait(0))
        return Results.Json(new { error = "Модель занята. Повторите запрос позже." }, statusCode: 503);
    try
    {
        // A disconnected client must not release the slot while Ollama is still running.
        // Keep the upstream request alive until completion or its own timeout.
        using var response = await ollama.PostAsJsonAsync("http://127.0.0.1:11434/api/chat", new
        {
            model = "qwen2.5:3b",
            messages = new[] { new { role = "user", content = request.Message } },
            options = new { num_ctx = 512, num_predict = 64 },
            stream = false
        });
        if (!response.IsSuccessStatusCode)
            return Results.Json(new { error = "Сервис модели недоступен или не смог обработать запрос." }, statusCode: 502);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!result.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
            !result.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            return Results.Json(new { error = "Сервис модели вернул некорректный ответ." }, statusCode: 502);
        return Results.Ok(new { response = content.GetString() });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Время ожидания ответа модели истекло." }, statusCode: 504);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { error = "Не удалось обратиться к сервису модели." }, statusCode: 502);
    }
    catch (JsonException)
    {
        return Results.Json(new { error = "Сервис модели вернул некорректный ответ." }, statusCode: 502);
    }
    finally { generation.Release(); }
}).WithMetadata(new PrivateChatEndpoint()).RequireRateLimiting("chat");

app.Run();

sealed record ChatRequest(string? Message);
sealed class PrivateChatEndpoint;
