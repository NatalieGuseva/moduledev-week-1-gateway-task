using System.Text.Json;
using Common.Contracts;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        NpgsqlDataSource dataSource,
        ILogger<HealthController> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    [HttpGet("live")]
    public IActionResult Live()
    {
        return Ok(new { status = "alive" });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            
            await using var connection = _dataSource.CreateConnection();
            await connection.OpenAsync(cts.Token);

            // Проверяем доступность БД
            var result = await connection.ExecuteScalarAsync<int>("SELECT 1", commandTimeout: 5);

            if (result == 1)
            {
                // Проверяем наличие таблицы action_catalog
                var tableExists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'course' AND table_name = 'action_catalog')",
                    commandTimeout: 5);

                if (tableExists)
                {
                    return Ok(new { status = "ready" });
                }
                else
                {
                    // 🔥 ИЗМЕНЕНО: LogWarning → LogDebug
                    _logger.LogDebug("Database is accessible but table 'action_catalog' does not exist");
                    return StatusCode(503, new { status = "unhealthy", reason = "Database not initialized" });
                }
            }

            return StatusCode(503, new { status = "unhealthy", reason = "Database check failed" });
        }
        catch (OperationCanceledException)
        {
            // 🔥 ИЗМЕНЕНО: LogWarning → LogDebug
            _logger.LogDebug("Readiness check timed out");
            return StatusCode(503, new { status = "unhealthy", reason = "Timeout" });
        }
        catch (NpgsqlException ex)
        {
            // 🔥 ИЗМЕНЕНО: LogWarning(ex, ...) → LogDebug(ex, ...)
            _logger.LogDebug(ex, "PostgreSQL is unavailable");
            return StatusCode(503, new { status = "unhealthy", reason = "Database unavailable" });
        }
        catch (Exception ex)
        {
            // 🔥 ИЗМЕНЕНО: LogInformation → LogDebug
            _logger.LogDebug(ex, "Unexpected error during readiness check");
            return StatusCode(503, new { status = "unhealthy", reason = "Internal error" });
        }
    }
}