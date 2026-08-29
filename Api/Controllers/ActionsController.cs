using System.Data;
using System.Text;
using System.Text.Json;
using Api.Middleware;
using Common.Contracts;
using Common.DTOs;
using Dapper;
using Json.Schema;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public class ActionsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ActionsController> _logger;

    public ActionsController(NpgsqlDataSource dataSource, ILogger<ActionsController> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    [HttpPost("{module}/{actionName}")]
    public async Task<IActionResult> InvokeAction(string module, string actionName, CancellationToken cancellationToken)
    {
        var correlationId = HttpContext.Items["CorrelationId"] is Guid cid ? cid : Guid.NewGuid();
        var trustedContext = HttpContext.Items["TrustedContext"] as TrustedContext;
        var requestDto = HttpContext.Items["ValidatedRequest"] as RequestDTO;
        var rawPayload = HttpContext.Items["RawPayloadString"] as string ?? "{}";
        var requestedVersion = HttpContext.Items["ActionVersion"] as int?;
        var idempotencyKey = HttpContext.Items["IdempotencyKey"] as string;

        if (trustedContext == null || requestDto == null)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, CreateErrorEnvelope("auth.invalid", "Authentication required", correlationId));
        }

        NpgsqlConnection connection;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogDebug(ex, "PostgreSQL database unavailable");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, CreateErrorEnvelope("dependency.unavailable", "PostgreSQL is unavailable", correlationId, requestedVersion));
        }

        await using (connection)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var manifest = await ResolveManifestAsync(connection, transaction, module, actionName, requestedVersion);
                if (manifest == null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return StatusCode(StatusCodes.Status404NotFound, CreateErrorEnvelope("action.not_found", $"Action {module}.{actionName} (version: {requestedVersion?.ToString() ?? "default"}) not found or disabled", correlationId, requestedVersion));
                }

                int effectiveVersion = manifest.Version;

                // Валидация request schema до вызова api.invoke
                if (!string.IsNullOrEmpty(manifest.RequestSchema) && manifest.RequestSchema != "{}" && manifest.RequestSchema != "[]")
                {
                    try
                    {
                        using var payloadDoc = JsonDocument.Parse(rawPayload);
                        var schema = JsonSchema.FromText(manifest.RequestSchema);
                        var evaluation = schema.Evaluate(payloadDoc.RootElement, new EvaluationOptions
                        {
                            OutputFormat = OutputFormat.List
                        });

                        if (!evaluation.IsValid)
                        {
                            var errors = CollectValidationErrors(evaluation);
                            var errorMessage = errors.Count > 0 ? string.Join("; ", errors) : "Unknown validation error";
                            _logger.LogWarning("Request schema validation failed: {Errors}", errorMessage);

                            await transaction.RollbackAsync(cancellationToken);
                            return StatusCode(StatusCodes.Status422UnprocessableEntity,
                                CreateErrorEnvelope("payload.invalid", $"Request payload does not match schema: {errorMessage}", correlationId, effectiveVersion));
                        }
                    }
                    catch (JsonException ex)
                    {
                        // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
                        _logger.LogDebug(ex, "Failed to parse request payload");
                        await transaction.RollbackAsync(cancellationToken);
                        return StatusCode(StatusCodes.Status422UnprocessableEntity,
                            CreateErrorEnvelope("payload.invalid", "Request payload is not valid JSON", correlationId, effectiveVersion));
                    }
                    catch (Exception ex)
                    {
                        // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
                        _logger.LogDebug(ex, "Failed to validate request schema");
                        await transaction.RollbackAsync(cancellationToken);
                        return StatusCode(StatusCodes.Status500InternalServerError,
                            CreateErrorEnvelope("internal.error", "Failed to validate request schema", correlationId, effectiveVersion));
                    }
                }

                if (manifest.IdempotencyMode == "required" && string.IsNullOrEmpty(idempotencyKey))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return StatusCode(StatusCodes.Status400BadRequest, CreateErrorEnvelope("idempotency.required", "Idempotency key is required for this action", correlationId, effectiveVersion));
                }

                if (!string.IsNullOrEmpty(manifest.RequiredPolicy) && manifest.RequiredPolicy != "[]")
                {
                    var allowedScopes = JsonSerializer.Deserialize<List<string>>(manifest.RequiredPolicy) ?? new List<string>();
                    // Политика — это набор ВСЕХ обязательных scope, а не "любой из". Раньше здесь
                    // стояла .Intersect(...).Any() (совпадение хотя бы одного), что расходилось с
                    // course.check_policy в БД (там ANDа: должен присутствовать каждый required
                    // scope). Пока в манифестах required_policy всегда из одного элемента разница
                    // не проявлялась, но на HTTP-границе это была более слабая проверка, чем
                    // повторная проверка внутри api.invoke — то есть defense-in-depth был нарушен.
                    if (allowedScopes.Any() && !allowedScopes.All(scope => trustedContext.Scopes.Contains(scope)))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return StatusCode(StatusCodes.Status403Forbidden, CreateErrorEnvelope("access.denied", "Access denied by action policy", correlationId, effectiveVersion));
                    }
                }

                if (!string.IsNullOrEmpty(idempotencyKey))
                {
                    var existingRecord = await CheckIdempotencyAsync(connection, transaction, idempotencyKey, module, actionName, effectiveVersion, trustedContext.Principal);
                    if (existingRecord != null)
                    {
                        if (existingRecord.PayloadHash != GetHash(rawPayload))
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            return StatusCode(StatusCodes.Status409Conflict, CreateErrorEnvelope("idempotency.conflict", "Idempotency key reused with different payload", correlationId, effectiveVersion));
                        }

                        await transaction.CommitAsync(cancellationToken);
                        return Ok(JsonSerializer.Deserialize<object>(existingRecord.ResultJson));
                    }
                }

                // Дособираем TrustedContext значениями, которые не были известны в JwtContextMiddleware:
                // - requestId берётся из заголовка Idempotency-Key (ADR 001), а не из payload;
                // - deadline вычисляется из timeout_ms манифеста, который разрешился только что.
                // Без requestId запись в course.operations падает на NOT NULL constraint для
                // любого action с idempotency_mode = required (payment.request, opencheck.probe).
                var effectiveContext = trustedContext with
                {
                    RequestId = idempotencyKey,
                    Deadline = DateTimeOffset.UtcNow.AddMilliseconds(manifest.TimeoutMs)
                };

                var contextJson = JsonSerializer.Serialize(effectiveContext, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var invokeResultJson = await InvokeApiFunctionAsync(connection, transaction, module, actionName, effectiveVersion, contextJson, rawPayload);

                using var doc = JsonDocument.Parse(invokeResultJson);
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "error";

                if (status == "ok")
                {
                    var outcome = root.TryGetProperty("outcome", out var oc) ? oc.GetString() ?? "" : "";
                    
                    if (!string.IsNullOrEmpty(manifest.OutcomesJson) && manifest.OutcomesJson != "[]")
                    {
                        var allowedOutcomes = JsonSerializer.Deserialize<List<string>>(manifest.OutcomesJson) ?? new List<string>();
                        if (!allowedOutcomes.Contains(outcome))
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            return StatusCode(StatusCodes.Status500InternalServerError, CreateErrorEnvelope("action.contract_violation", $"Outcome '{outcome}' is not declared in manifest", correlationId, effectiveVersion));
                        }
                    }

                    // Получаем result элемент из документа
                    var resultElement = root.TryGetProperty("result", out var res) ? res : default;

                    // Безопасное копирование: используем JsonDocument и Clone()
                    JsonElement copiedResult = default;
                    if (resultElement.ValueKind != JsonValueKind.Undefined && resultElement.ValueKind != JsonValueKind.Null)
                    {
                        try
                        {
                            using var tempDoc = JsonDocument.Parse(resultElement.GetRawText());
                            copiedResult = tempDoc.RootElement.Clone();
                        }
                        catch
                        {
                            using var emptyDoc = JsonDocument.Parse("{}");
                            copiedResult = emptyDoc.RootElement.Clone();
                        }
                    }
                    else
                    {
                        using var emptyDoc = JsonDocument.Parse("{}");
                        copiedResult = emptyDoc.RootElement.Clone();
                    }

                    // ✅ ВАЛИДАЦИЯ RESPONSE SCHEMA (для ВСЕХ actions, включая opencheck.probe)
                    if (!string.IsNullOrEmpty(manifest.ResponseSchema) && manifest.ResponseSchema != "{}" && manifest.ResponseSchema != "[]")
                    {
                        try
                        {
                            var schema = JsonSchema.FromText(manifest.ResponseSchema);
                            var evaluation = schema.Evaluate(copiedResult, new EvaluationOptions
                            {
                                OutputFormat = OutputFormat.List
                            });
                            
                            if (!evaluation.IsValid)
                            {
                                var errors = CollectValidationErrors(evaluation);
                                var errorMessage = errors.Count > 0 ? string.Join("; ", errors) : "Unknown validation error";
                                _logger.LogWarning("Response schema validation failed: {Errors}", errorMessage);
                                
                                await transaction.RollbackAsync(cancellationToken);
                                return StatusCode(StatusCodes.Status500InternalServerError, 
                                    CreateErrorEnvelope("action.contract_violation", $"Result does not match response schema: {errorMessage}", correlationId, effectiveVersion));
                            }
                        }
                        catch (Exception ex)
                        {
                            // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
                            _logger.LogDebug(ex, "Failed to validate response schema");
                            await transaction.RollbackAsync(cancellationToken);
                            return StatusCode(StatusCodes.Status500InternalServerError, 
                                CreateErrorEnvelope("action.contract_violation", "Failed to validate response schema", correlationId, effectiveVersion));
                        }
                    }

                    // Создаем успешный ответ с скопированным результатом
                    var finalSuccessResponse = new
                    {
                        status = "ok",
                        outcome,
                        result = copiedResult,
                        meta = new
                        {
                            correlationId,
                            actionVersion = effectiveVersion
                        }
                    };
                    var finalSuccessJson = JsonSerializer.Serialize(finalSuccessResponse);

                    if (!string.IsNullOrEmpty(idempotencyKey))
                    {
                        await SaveIdempotencyAsync(connection, transaction, idempotencyKey, module, actionName, effectiveVersion, trustedContext.Principal, GetHash(rawPayload), finalSuccessJson);
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return Ok(finalSuccessResponse);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);

                    var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal.error" : "internal.error";
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "Action failed" : "Action failed";
                    
                    var httpStatus = GetHttpStatusFromCode(code);
                    return StatusCode(httpStatus, CreateErrorEnvelope(code, message, correlationId, effectiveVersion));
                }
            }
            catch (NpgsqlException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
                _logger.LogDebug(ex, "PostgreSQL execution failed");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, CreateErrorEnvelope("dependency.unavailable", "PostgreSQL database error during execution", correlationId, requestedVersion));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
                _logger.LogDebug(ex, "Unexpected error executing action");
                return StatusCode(StatusCodes.Status500InternalServerError, CreateErrorEnvelope("internal.error", "Internal execution error", correlationId, requestedVersion));
            }
        }
    }

    // Вспомогательный метод для сбора ошибок валидации
    private static List<string> CollectValidationErrors(EvaluationResults evaluation)
    {
        var errors = new List<string>();
        
        if (evaluation.Errors != null)
        {
            foreach (var error in evaluation.Errors)
            {
                errors.Add($"{error.Key}: {error.Value}");
            }
        }
        
        if (errors.Count == 0 && evaluation.Details != null)
        {
            foreach (var detail in evaluation.Details)
            {
                if (detail.Errors != null)
                {
                    foreach (var error in detail.Errors)
                    {
                        errors.Add($"{error.Key}: {error.Value}");
                    }
                }
            }
        }
        
        return errors;
    }

    private async Task<ActionManifestDb?> ResolveManifestAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string module, string action, int? version)
    {
        string sql;
        if (version.HasValue)
        {
            sql = @"
                SELECT module, action, version, http_method as HttpMethod, request_schema::text as RequestSchema, 
                       response_schema::text as ResponseSchema, outcomes::text as OutcomesJson, 
                       required_policy::text as RequiredPolicy, idempotency_mode as IdempotencyMode,
                       timeout_ms as TimeoutMs
                FROM course.action_catalog
                WHERE module = @module AND action = @action AND version = @version AND enabled = true";
            return await conn.QueryFirstOrDefaultAsync<ActionManifestDb>(sql, new { module, action, version = version.Value }, tx);
        }

        sql = @"
            SELECT module, action, version, http_method as HttpMethod, request_schema::text as RequestSchema, 
                   response_schema::text as ResponseSchema, outcomes::text as OutcomesJson, 
                   required_policy::text as RequiredPolicy, idempotency_mode as IdempotencyMode,
                   timeout_ms as TimeoutMs
            FROM course.action_catalog
            WHERE module = @module AND action = @action AND is_default = true AND enabled = true";
        return await conn.QueryFirstOrDefaultAsync<ActionManifestDb>(sql, new { module, action }, tx);
    }

    private async Task<string> InvokeApiFunctionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string module, string action, int version, string contextJson, string payloadJson)
    {
        const string sql = "SELECT api.invoke(@module, @action, @version, @context::jsonb, @payload::jsonb)";
        var res = await conn.ExecuteScalarAsync<string>(sql, new { module, action, version, context = contextJson, payload = payloadJson }, tx);
        return res ?? "{}";
    }

    private async Task<IdempotencyRecordDb?> CheckIdempotencyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, string module, string action, int version, string principal)
    {
        const string sql = @"
            SELECT payload_hash as PayloadHash, result as ResultJson
            FROM course.idempotency_records
            WHERE idempotency_key = @key AND module = @module AND action = @action AND version = @version AND principal = @principal";
        return await conn.QueryFirstOrDefaultAsync<IdempotencyRecordDb>(sql, new { key, module, action, version, principal }, tx);
    }

    private async Task SaveIdempotencyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, string module, string action, int version, string principal, string hash, string resultJson)
    {
        const string sql = @"
            INSERT INTO course.idempotency_records (idempotency_key, module, action, version, principal, payload_hash, result, status, created_at)
            VALUES (@key, @module, @action, @version, @principal, @hash, @resultJson::jsonb, 'OK', NOW())
            ON CONFLICT (idempotency_key, module, action, version, principal) DO NOTHING";
        await conn.ExecuteAsync(sql, new { key, module, action, version, principal, hash, resultJson }, tx);
    }

    private static string GetHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToBase64String(bytes);
    }

    private static int GetHttpStatusFromCode(string code) => code switch
    {
        "access.denied" => StatusCodes.Status403Forbidden,
        "action.not_found" => StatusCodes.Status404NotFound,
        "idempotency.conflict" => StatusCodes.Status409Conflict,
        "payload.invalid" => StatusCodes.Status422UnprocessableEntity,
        "action.timeout" => StatusCodes.Status504GatewayTimeout,
        "dependency.unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    private static ErrorEnvelope CreateErrorEnvelope(string code, string message, Guid correlationId, int? version = null) => new()
    {
        Code = code,
        Message = message,
        Retryable = code == "dependency.unavailable" || code == "action.timeout",
        Meta = new Meta { CorrelationId = correlationId, ActionVersion = version }
    };

    private class ActionManifestDb
    {
        public string Module { get; set; } = "";
        public string Action { get; set; } = "";
        public int Version { get; set; }
        public string HttpMethod { get; set; } = "POST";
        public string RequestSchema { get; set; } = "{}";
        public string ResponseSchema { get; set; } = "{}";
        public string OutcomesJson { get; set; } = "[]";
        public string RequiredPolicy { get; set; } = "[]";
        public string IdempotencyMode { get; set; } = "none";
        public int TimeoutMs { get; set; } = 30000;
    }

    private class IdempotencyRecordDb
    {
        public string PayloadHash { get; set; } = "";
        public string ResultJson { get; set; } = "";
    }
}
