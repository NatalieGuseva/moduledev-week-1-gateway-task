using System.Text;
using System.Text.Json;
using Common.Contracts;
using Common.DTOs;

namespace Api.Middleware;

public class JsonSchemaValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JsonSchemaValidationMiddleware> _logger;

    public JsonSchemaValidationMiddleware(
        RequestDelegate next,
        ILogger<JsonSchemaValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // КЛЮЧЕВОЙ ФИКС: Если ответ уже начал формироваться - пропускаем
        if (context.Response.HasStarted)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var correlationId = context.Items["CorrelationId"] is Guid cid ? cid : Guid.NewGuid();

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "request.invalid", "Only POST method is allowed", correlationId);
            return;
        }

        if (context.Request.ContentType == null || !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "request.invalid", "Content-Type must be application/json", correlationId);
            return;
        }

        var trustedContext = context.Items["TrustedContext"] as TrustedContext;
        if (trustedContext == null)
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "auth.invalid", "Trusted context not found", correlationId);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length != 3 || !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status404NotFound, "action.not_found", "Invalid action route format", correlationId);
            return;
        }

        var module = segments[1];
        var action = segments[2];

        // Разбираем версию из X-Action-Version
        int? actionVersion = null;
        if (context.Request.Headers.TryGetValue("X-Action-Version", out var versionHeader))
        {
            if (!int.TryParse(versionHeader, out var version) || version <= 0)
            {
                await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "request.invalid", "X-Action-Version must be a positive integer", correlationId);
                return;
            }
            actionVersion = version;
        }

        // Проверяем наличие заголовок Idempotency-Key
        string? idempotencyKey = null;
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyHeader))
        {
            idempotencyKey = idempotencyHeader.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "idempotency.required", "Idempotency-Key header cannot be empty", correlationId);
                return;
            }
        }

        context.Request.EnableBuffering();
        var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
        context.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "payload.invalid", "Request body cannot be empty", correlationId, actionVersion);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Валидация корневой структуры payload
            var requestDto = new RequestDTO { Payload = root.Clone() };
            context.Items["ValidatedRequest"] = requestDto;
            context.Items["RawPayloadString"] = body;
        }
        catch (JsonException)
        {
            await CorrelationAndErrorMiddleware.WriteErrorAsync(context, StatusCodes.Status422UnprocessableEntity, "payload.invalid", "Invalid JSON format in payload", correlationId, actionVersion);
            return;
        }

        context.Items["Module"] = module;
        context.Items["Action"] = action;
        context.Items["ActionVersion"] = actionVersion;
        context.Items["IdempotencyKey"] = idempotencyKey;

        await _next(context);
    }
}