using Npgsql;
using System.Data;
using System.Data.Common;
using Lib.ServerTiming;
using AkariApi.Helpers;
using Microsoft.Extensions.Logging;

namespace AkariApi.Services;

public class PostgresService : IDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly ILogger<PostgresService> _logger;
    private readonly IServerTiming? _serverTiming;

    public PostgresService(IConfiguration configuration, ILogger<PostgresService> logger, IServerTiming? serverTiming = null)
    {
        _logger = logger;
        _serverTiming = serverTiming;
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

    /// <summary>
    /// Returns a timing-aware <see cref="DbConnection"/> for use with Dapper queries.
    /// When server timing is active each executed command is recorded as a <c>db</c> metric.
    /// </summary>
    public DbConnection Connection => _serverTiming != null
        ? new TimedDbConnection(_connection, _serverTiming)
        : _connection;

    /// <summary>
    /// The raw underlying <see cref="NpgsqlConnection"/> for operations that require
    /// Npgsql-specific types, such as constructing <see cref="NpgsqlCommand"/> directly.
    /// </summary>
    public NpgsqlConnection NpgsqlConnection => _connection;

    public void Dispose()
    {
        _connection.Dispose();
    }
}