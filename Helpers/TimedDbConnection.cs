using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lib.ServerTiming;
using Npgsql;

namespace AkariApi.Helpers;

/// <summary>
/// Wraps a <see cref="NpgsqlConnection"/> and automatically times every command
/// execution, adding a <c>db</c> metric to the <see cref="IServerTiming"/> header
/// for the current request. Created transparently by <see cref="Services.PostgresService"/>
/// — no controller or query-site changes required.
/// </summary>
internal sealed class TimedDbConnection : DbConnection
{
    private readonly NpgsqlConnection _inner;
    private readonly IServerTiming _serverTiming;

    internal TimedDbConnection(NpgsqlConnection inner, IServerTiming serverTiming)
    {
        _inner = inner;
        _serverTiming = serverTiming;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value!;
    }
    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;

    public override void Open() => _inner.Open();
    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);
    public override void Close() => _inner.Close();
    public override Task CloseAsync() => _inner.CloseAsync();
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        _inner.BeginTransaction(isolationLevel);

    // Dapper calls CreateCommand() → CreateDbCommand(). Return a timed command so
    // every query execution is measured without touching any call site.
    protected override DbCommand CreateDbCommand() =>
        new TimedDbCommand(_inner.CreateCommand(), _serverTiming);

    // Do NOT dispose the inner connection – PostgresService owns its lifetime.
    protected override void Dispose(bool disposing) { }
}

/// <summary>
/// Wraps a <see cref="NpgsqlCommand"/> and records each execution duration as a
/// <c>db</c> server timing metric.
/// </summary>
internal sealed class TimedDbCommand : DbCommand
{
    private readonly NpgsqlCommand _inner;
    private readonly IServerTiming _serverTiming;

    internal TimedDbCommand(NpgsqlCommand inner, IServerTiming serverTiming)
    {
        _inner = inner;
        _serverTiming = serverTiming;
    }

    // ── Property delegation ──────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value!;
    }
    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }
    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }
    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }
    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }
    protected override DbConnection? DbConnection
    {
        get => _inner.Connection;
        set => _inner.Connection = value is null or NpgsqlConnection
            ? (NpgsqlConnection?)value
            : throw new ArgumentException($"Expected {nameof(NpgsqlConnection)}, got {value.GetType().Name}.");
    }
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value is null or NpgsqlTransaction
            ? (NpgsqlTransaction?)value
            : throw new ArgumentException($"Expected {nameof(NpgsqlTransaction)}, got {value.GetType().Name}.");
    }

    public override void Cancel() => _inner.Cancel();
    public override void Prepare() => _inner.Prepare();
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    // ── Timed execution (sync) ───────────────────────────────────────────────

    public override int ExecuteNonQuery()
    {
        var sw = Stopwatch.StartNew();
        try { return _inner.ExecuteNonQuery(); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }

    public override object? ExecuteScalar()
    {
        var sw = Stopwatch.StartNew();
        try { return _inner.ExecuteScalar(); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var sw = Stopwatch.StartNew();
        try { return _inner.ExecuteReader(behavior); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }

    // ── Timed execution (async) ──────────────────────────────────────────────
    // Dapper checks whether the command is a DbCommand and calls the async overrides
    // below, so these are the paths exercised for all awaited Dapper calls.

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try { return await _inner.ExecuteNonQueryAsync(cancellationToken); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try { return await _inner.ExecuteScalarAsync(cancellationToken); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try { return await _inner.ExecuteReaderAsync(behavior, cancellationToken); }
        finally { _serverTiming.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "db"); }
    }
}
