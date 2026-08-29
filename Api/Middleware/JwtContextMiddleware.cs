using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Common.Contracts;

namespace Api.Middleware;

public class JwtContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JwtContextMiddleware> _logger;
    private readonly string _signingKey;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtContextMiddleware(
        RequestDelegate next,
        ILogger<JwtContextMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _signingKey = configuration["COURSE_JWT_SIGNING_KEY"] ?? 
                      configuration["Course:Jwt:SigningKey"] ?? 
                      throw new InvalidOperationException("JWT signing key is not configured");
        _issuer = configuration["COURSE_JWT_ISSUER"] ?? "moduledev-course";
        _audience = configuration["COURSE_JWT_AUDIENCE"] ?? "moduledev-api";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Проверяем наличие токена
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length).Trim();
                var principal = ValidateToken(token);
                
                if (principal != null)
                {
                    context.User = principal;

                    // correlationId — единая точка генерации на весь HTTP-запрос.
                    // CorrelationAndErrorMiddleware уже создал (или принял из заголовка) UUID
                    // и положил его в context.Items до вызова этого middleware.
                    // Токен НИКОГДА не несёт claim "correlationId" — читать его отсюда некорректно
                    // и на практике всегда давало новый случайный Guid, расходящийся с тем,
                    // что уходит клиенту в X-Correlation-ID и error envelope.
                    var correlationId = context.Items["CorrelationId"] is Guid cid ? cid : Guid.NewGuid();

                    // scope — стандартный OAuth2 claim: ОДНА строка со scope'ами через пробел
                    // (см. RFC 6749 §5.1 и autocheck/public_check.py: "scope": " ".join(scopes)).
                    // FindAll("scope") находит один claim с этой строкой целиком — её нужно
                    // разбить по пробелам, иначе required_policy никогда не совпадёт ни с чем,
                    // кроме токена с ровно одним scope.
                    var scopes = principal.FindAll("scope")
                        .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Distinct()
                        .ToArray();

                    // principal.Identity?.Name смотрит на claim ClaimTypes.Name ("...claims/name"),
                    // а в токене есть только "sub" — он туда не маппится (MapInboundClaims здесь
                    // выключен явно в ValidateToken, чтобы не зависеть от версии библиотеки).
                    // Из-за этого Identity.Name всегда был null, и principal становился "unknown"
                    // независимо от того, кто прислал токен — generic-action-default-v1 как раз
                    // проверяет, что вернувшийся principal равен реальному "sub" из токена.
                    //
                    // sub и consumer — обязательные непустые строковые claim'ы (contract-reference.md,
                    // строки 144, 160). Молчаливая подстановка дефолтов ("unknown" / "") маскировала
                    // бы отсутствие или неверный тип claim'а и пропускала запрос дальше вместо 401.
                    // Поэтому здесь — явная проверка: при отсутствии/пустом/нестроковом значении
                    // TrustedContext не устанавливается, и JsonSchemaValidationMiddleware ниже по
                    // цепочке сам вернёт 401 auth.invalid ("Trusted context not found").
                    var subjectClaim = principal.FindFirst("sub");
                    var consumerClaim = principal.FindFirst("consumer");
                    var subject = subjectClaim?.Value;
                    var consumer = consumerClaim?.Value;

                    // ValueType != ClaimValueTypes.String означает, что JwtSecurityTokenHandler
                    // распознал в payload не JSON-строку, а число/bool/объект/массив —
                    // это и есть "неверный JSON-тип", который контракт требует ловить как 401.
                    var subjectValid = subjectClaim != null
                        && subjectClaim.ValueType == ClaimValueTypes.String
                        && !string.IsNullOrWhiteSpace(subject);
                    var consumerValid = consumerClaim != null
                        && consumerClaim.ValueType == ClaimValueTypes.String
                        && !string.IsNullOrWhiteSpace(consumer);

                    if (!subjectValid || !consumerValid)
                    {
                        _logger.LogDebug(
                            "Missing or invalid required claim(s): sub={SubjectValid}, consumer={ConsumerValid}",
                            subjectValid, consumerValid);
                    }
                    else
                    {
                        // Используем существующий TrustedContext из Common.Contracts
                        var trustedContext = new TrustedContext
                        {
                            Principal = subject!,
                            Consumer = consumer!,
                            Scopes = scopes,
                            CorrelationId = correlationId
                            // RequestId и Deadline заполняются позже, в ActionsController:
                            // RequestId = заголовок Idempotency-Key (доступен только в
                            // JsonSchemaValidationMiddleware, который выполняется после этого),
                            // Deadline = now + timeout_ms манифеста (манифест разрешается уже
                            // внутри контроллера, до middleware о нём ничего не известно).
                        };
                        context.Items["TrustedContext"] = trustedContext;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
            _logger.LogDebug(ex, "JWT validation failed");
        }

        await _next(context);
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var key = Encoding.UTF8.GetBytes(_signingKey);
            var tokenHandler = new JwtSecurityTokenHandler
            {
                // Отключаем legacy-маппинг входящих claim'ов (sub -> ClaimTypes.NameIdentifier
                // и т.п.), чтобы claim'ы из токена были доступны под теми же именами, что и в
                // payload ("sub", "consumer", "scope") — предсказуемо и не зависит от версии
                // System.IdentityModel.Tokens.Jwt.
                MapInboundClaims = false
            };
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            
            if (principal.Identity?.IsAuthenticated != true)
            {
                _logger.LogDebug("Token validation failed: principal not authenticated");
                return null;
            }

            return principal;
        }
        catch (Exception ex)
        {
            // 🔥 ИСПРАВЛЕНО: LogError → LogDebug
            _logger.LogDebug(ex, "Token validation failed");
            return null;
        }
    }
}
