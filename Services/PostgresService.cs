using Npgsql;
using System.Data;

namespace AkariApi.Services;

public class PostgresService : IDisposable
{
    private readonly NpgsqlConnection _connection;

    public PostgresService(IConfiguration configuration)
    {
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
            await _connection.OpenAsync();
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