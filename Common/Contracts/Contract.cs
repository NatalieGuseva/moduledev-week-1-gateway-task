using System;
using System.Text.Json.Serialization;

namespace Common.Contracts;

// ============================================================
// TransportContext — trusted markers, которые generic C# signature boundary
// (Api/Middleware/ProviderSignatureMiddleware) кладёт в TrustedContext после
// успешной проверки X-Provider-Signature. По 04_assignment.md target
// (payment.receipt_accept) получает только эти два маркера:
//   - transport.signatureVerified — подпись проверена;
//   - transport.signatureVersion  — версия подписи (сейчас всегда "v1").
// Target сам решает, обязательна ли подпись для его action, и возвращает
// 403 receipt.signature_required, если её нет (см. 07-autocheck-outline.md).
// ============================================================
public record TransportContext
{
    [JsonPropertyName("signatureVerified")]
    public bool SignatureVerified { get; init; }

    [JsonPropertyName("signatureVersion")]
    public string? SignatureVersion { get; init; }
}

public record TrustedContext
{
    [JsonPropertyName("principal")]
    public string Principal { get; init; } = string.Empty;

    [JsonPropertyName("consumer")]
    public string Consumer { get; init; } = string.Empty;

    [JsonPropertyName("scopes")]
    public string[] Scopes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("correlationId")]
    public Guid CorrelationId { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("deadline")]
    public DateTimeOffset Deadline { get; init; }

    // Заполняются только Workflow.Worker при вызове action изнутри workflow —
    // для обычных HTTP-вызовов через Api остаются null и просто не попадают
    // в сериализованный JSON. Обязательны по 04_assignment.md: "Worker создаёт
    // trusted context principal workflow-worker, добавляет processId, jobId,
    // executionId, attemptId". executionId используется целевыми функциями
    // как ключ идемпотентности предметного эффекта (см. probe-fixture недели 2:
    // p_context ->> 'executionId' используется как PRIMARY KEY).
    [JsonPropertyName("processId")]
    public Guid? ProcessId { get; init; }

    [JsonPropertyName("jobId")]
    public Guid? JobId { get; init; }

    [JsonPropertyName("executionId")]
    public Guid? ExecutionId { get; init; }

    [JsonPropertyName("attemptId")]
    public Guid? AttemptId { get; init; }

    // Заполняется только ProviderSignatureMiddleware (Api) после успешной
    // проверки X-Provider-Signature. Для всех остальных вызовов (JWT-only,
    // Workflow.Worker) остаётся null и не попадает в сериализованный JSON.
    // Target-функции (receipt.accept) читают p_context -> 'transport' ->>
    // 'signatureVerified' / 'signatureVersion' и решают, обязательна ли подпись.
    [JsonPropertyName("transport")]
    public TransportContext? Transport { get; init; }
}

public record Meta
{
    [JsonPropertyName("correlationId")]
    public Guid CorrelationId { get; init; }

    [JsonPropertyName("actionVersion")]
    public int? ActionVersion { get; init; }
}

public record SuccessEnvelope<T>
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public T Result { get; init; } = default!;

    [JsonPropertyName("meta")]
    public Meta Meta { get; init; } = null!;
}

public record ErrorEnvelope
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    [JsonPropertyName("details")]
    public object? Details { get; init; }

    [JsonPropertyName("meta")]
    public Meta Meta { get; init; } = null!;
}