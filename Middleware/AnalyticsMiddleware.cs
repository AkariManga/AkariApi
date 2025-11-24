namespace Analytics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AkariApi.Services;
using Npgsql;

public class Config
{
    public Func<HttpContext, string>? GetPath { get; set; } = null;
    public Func<HttpContext, string>? GetIPAddress { get; set; } = null;
    public Func<HttpContext, string>? GetHostname { get; set; } = null;
    public Func<HttpContext, string>? GetUserAgent { get; set; } = null;
    public int PrivacyLevel { get; set; } = 0;
}

public class AnalyticsService : IDisposable
{
    private readonly Config _config;
    private readonly ILogger<AnalyticsService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentQueue<RequestData> _requests = new();
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private bool _disposed = false;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    public AnalyticsService(IServiceProvider serviceProvider, Config config, ILogger<AnalyticsService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _flushTimer = new Timer(async _ => await FlushRequestsAsync(), null, FlushInterval, FlushInterval);
    }

    private struct Payload
    {
        [JsonPropertyName("requests")]
        public List<RequestData> Requests { get; set; }

        [JsonPropertyName("framework")]
        public string Framework { get; set; }

        [JsonPropertyName("privacy_level")]
        public int PrivacyLevel { get; set; }
    }

    public struct RequestData
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; }

        [JsonPropertyName("ip_address")]
        public string IPAddress { get; set; }

        [JsonPropertyName("user_agent")]
        public string UserAgent { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("response_time")]
        public int ResponseTime { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        public override readonly string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }

    private async Task InsertRequestsAsync(List<RequestData> requests)
    {
        if (requests.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        var postgresService = scope.ServiceProvider.GetRequiredService<PostgresService>();

        await postgresService.OpenAsync();

        try
        {
            using var cmd = new NpgsqlCommand();
            cmd.Connection = postgresService.Connection;

            var query = "INSERT INTO analytics_requests (hostname, ip_address, user_agent, path, method, response_time, status, created_at) VALUES ";
            var parameters = new List<NpgsqlParameter>();

            for (int i = 0; i < requests.Count; i++)
            {
                if (i > 0) query += ", ";
                query += $"(@h{i}, @ip{i}, @ua{i}, @p{i}, @m{i}, @rt{i}, @s{i}, @ca{i})";
                parameters.Add(new NpgsqlParameter($"@h{i}", requests[i].Hostname));
                parameters.Add(new NpgsqlParameter($"@ip{i}", requests[i].IPAddress));
                parameters.Add(new NpgsqlParameter($"@ua{i}", requests[i].UserAgent));
                parameters.Add(new NpgsqlParameter($"@p{i}", requests[i].Path));
                parameters.Add(new NpgsqlParameter($"@m{i}", requests[i].Method));
                parameters.Add(new NpgsqlParameter($"@rt{i}", requests[i].ResponseTime));
                parameters.Add(new NpgsqlParameter($"@s{i}", requests[i].Status));
                parameters.Add(new NpgsqlParameter($"@ca{i}", requests[i].CreatedAt));
            }

            cmd.CommandText = query;
            cmd.Parameters.AddRange(parameters.ToArray());

            await cmd.ExecuteNonQueryAsync();

            _logger.LogDebug("Successfully inserted {Count} analytics requests", requests.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while inserting analytics data");
        }
    }

    private async Task FlushRequestsAsync()
    {
        if (!_flushSemaphore.Wait(0)) // Non-blocking wait
            return; // A flush is already in progress, skip this tick.

        try
        {
            var requestsToFlush = new List<RequestData>();
            while (_requests.TryDequeue(out var request))
                requestsToFlush.Add(request);

            if (requestsToFlush.Count > 0)
                await InsertRequestsAsync(requestsToFlush);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during timed flush of analytics data.");
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    public void LogRequest(RequestData requestData)
    {
        _requests.Enqueue(requestData);
        // No immediate flush - only timer-based flushing every 60 seconds
    }

    public RequestData CreateRequestData(HttpContext context, long responseTimeMs, DateTime createdAt)
    {
        return new RequestData
        {
            Hostname = GetHostname(context),
            IPAddress = GetIPAddress(context),
            UserAgent = GetUserAgent(context),
            Path = GetPath(context),
            Method = context.Request.Method,
            ResponseTime = (int)responseTimeMs,
            Status = context.Response.StatusCode,
            CreatedAt = createdAt
        };
    }

    private string GetIPAddress(HttpContext context)
    {
        if (_config.PrivacyLevel >= 2)
            return "";

        try
        {
            if (_config.GetIPAddress != null)
                return _config.GetIPAddress.Invoke(context) ?? "";

            // Check for forwarded IP addresses first
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                // Take the first IP if multiple are present
                var firstIp = forwardedFor.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(firstIp))
                    return firstIp;
            }

            return context.Connection.RemoteIpAddress?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string GetHostname(HttpContext context)
    {
        try
        {
            if (_config.GetHostname != null)
                return _config.GetHostname.Invoke(context) ?? "";
            return context.Request.Host.ToString();
        }
        catch
        {
            return "";
        }
    }

    private string GetUserAgent(HttpContext context)
    {
        try
        {
            if (_config.GetUserAgent != null)
                return _config.GetUserAgent.Invoke(context) ?? "";
            return context.Request.Headers.UserAgent.ToString();
        }
        catch
        {
            return "";
        }
    }

    private string GetPath(HttpContext context)
    {
        try
        {
            if (_config.GetPath != null)
                return _config.GetPath.Invoke(context) ?? "";
            return context.Request.Path.ToString();
        }
        catch
        {
            return "";
        }
    }

    public async Task FlushAsync()
    {
        await _flushSemaphore.WaitAsync();
        try
        {
            var allRequests = new List<RequestData>();
            while (_requests.TryDequeue(out var request))
                allRequests.Add(request);

            if (allRequests.Count > 0)
                await InsertRequestsAsync(allRequests);
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _flushTimer?.Dispose();

            // Flush any remaining requests synchronously
            try
            {
                FlushAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while flushing analytics data during disposal");
            }

            _flushSemaphore?.Dispose();
            _disposed = true;
        }
    }
}

public class AnalyticsMiddleware(RequestDelegate next, AnalyticsService analyticsService, ILogger<AnalyticsMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly AnalyticsService _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
    private readonly ILogger<AnalyticsMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        var watch = Stopwatch.StartNew();
        var createdAt = DateTime.UtcNow;

        try
        {
            await _next(context);
        }
        finally
        {
            watch.Stop();

            try
            {
                var requestData = _analyticsService.CreateRequestData(context, watch.ElapsedMilliseconds, createdAt);
                _analyticsService.LogRequest(requestData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while logging analytics data");
            }
        }
    }
}

public static class AnalyticsExtensions
{
    public static IApplicationBuilder UseAnalytics(this IApplicationBuilder app, Config? config = null)
    {
        // Create the analytics service
        var loggerFactory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<AnalyticsService>();
        var analyticsService = new AnalyticsService(app.ApplicationServices, config ?? new Config(), logger);

        // Handle disposal during application shutdown
        var appLifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
        appLifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                logger.LogInformation("Analytics service is stopping, flushing remaining data...");
                analyticsService.FlushAsync().GetAwaiter().GetResult();
                analyticsService.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while disposing analytics service during shutdown");
            }
        });

        return app.Use(async (context, next) =>
        {
            var middlewareLogger = loggerFactory.CreateLogger<AnalyticsMiddleware>();
            var middleware = new AnalyticsMiddleware(
                async (ctx) => await next(),
                analyticsService,
                middlewareLogger
            );
            await middleware.InvokeAsync(context);
        });
    }
}

public class AnalyticsBackgroundService(AnalyticsService analyticsService, ILogger<AnalyticsBackgroundService> logger) : BackgroundService
{
    private readonly AnalyticsService _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
    private readonly ILogger<AnalyticsBackgroundService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service mainly exists to ensure proper cleanup during shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analytics service is stopping, flushing remaining data...");
        try
        {
            await _analyticsService.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while flushing analytics data during shutdown");
        }
        await base.StopAsync(cancellationToken);
    }
}