using System.Text.Json;
using Common.Contracts;

namespace Api.Middleware;

public class CorrelationAndErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationAndErrorMiddleware> _logger;

    public CorrelationAndErrorMiddleware(RequestDelegate next, ILogger<CorrelationAndErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Извлекаем или генерируем Correlation ID
        var correlationIdStr = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (!Guid.TryParse(correlationIdStr, out var correlationId) || correlationId == Guid.Empty)
        {
            correlationId = Guid.NewGuid();
        }

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId.ToString();

        try
        {
            await _next(context);

            // Если маршрут не найден (404) от инфраструктуры ASP.NET без тела
            if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status404NotFound)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "action.not_found", "Requested action route or endpoint not found", correlationId);
            }
        }
        catch (Exception ex)
        {
            // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
            _logger.LogDebug(ex, "Unhandled exception during request execution");
            if (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal.error", "An internal server error occurred", correlationId);
            }
        }
    }

    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message, Guid correlationId, int? actionVersion = null)
    {
        // КЛЮЧЕВОЙ ФИКС: НЕ перезаписываем ответ, если он уже начал формироваться
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var errorResponse = new ErrorEnvelope
        {
            Code = code,
            Message = message,
            Retryable = code == "dependency.unavailable" || code == "action.timeout",
            Details = null,
            Meta = new Meta
            {
                CorrelationId = correlationId,
                ActionVersion = actionVersion
            }
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse, jsonOptions));
    }
}