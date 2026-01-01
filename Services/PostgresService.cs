using Npgsql;
using System.Data;
using Microsoft.Extensions.Logging;

namespace AkariApi.Services;

public class PostgresService : IDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly ILogger<PostgresService> _logger;

    public PostgresService(IConfiguration configuration, ILogger<PostgresService> logger)
    {
        _logger = logger;
        var connectionString = configuration["POSTGRES_CONNECTION_STRING"] ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Postgres connection string is not set.");
        }
        _connection = new NpgsqlConnection(connectionString);
    }

    public async Task OpenAsync()
    {
        if (_connection.State != ConnectionState.Open)
        {
            try
            {
                await _connection.OpenAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open database connection");
                throw;
            }
        }
    }

    public async Task CloseAsync()
    {
        if (_connection.State != ConnectionState.Closed)
        {
            await _connection.CloseAsync();
        }
    }

    public NpgsqlConnection Connection => _connection;

    public void Dispose()
    {
        _connection.Dispose();
    }
}