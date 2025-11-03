using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AkariApi.Middleware
{
    public class RequestTimingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestTimingMiddleware> _logger;
        private const int MaxSamplesPerEndpoint = 500;
        private static readonly ConcurrentDictionary<string, RollingStats> Stats = new();

        public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var sw = Stopwatch.StartNew();
            int statusCode = 500;

            try
            {
                await _next(context);
                statusCode = context.Response.StatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in {Path}", context.Request.Path);
                throw;
            }
            finally
            {
                sw.Stop();
                var elapsed = sw.ElapsedMilliseconds;
                var method = context.Request.Method;
                var path = context.GetEndpoint()?.DisplayName ?? context.Request.Path;
                var key = $"{method} {path}";

                var stats = Stats.GetOrAdd(key, _ => new RollingStats(MaxSamplesPerEndpoint));
                stats.Add(elapsed);

                _ = Task.Run(() => SaveRouteStatsAsync(key, stats));

                _logger.LogInformation(
                    "{Method} {Path} ({Status}) took {Elapsed} ms (avg {Avg} ms over {Count} samples)",
                    method, path, statusCode, elapsed, stats.Average, stats.Count);
            }
        }

        private async Task SaveRouteStatsAsync(string key, RollingStats stats)
        {
            try
            {
                var data = new { stats.Count, stats.Average };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                var fileName = key.Replace("/", "_").Replace(" ", "_").Replace("?", "_").Replace("&", "_").Replace("=", "_") + ".json";
                var filePath = Path.Combine("stats", fileName);
                Directory.CreateDirectory("stats");
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save stats for {Key}", key);
            }
        }

        private sealed class RollingStats
        {
            private readonly long[] _samples;
            private int _index;
            private int _count;
            private long _sum;
            private readonly object _lock = new();

            public RollingStats(int capacity)
            {
                _samples = new long[capacity];
            }

            public int Count => _count;
            public double Average => _count == 0 ? 0 : (double)_sum / _count;

            public void Add(long value)
            {
                lock (_lock)
                {
                    if (_count < _samples.Length)
                    {
                        _samples[_count++] = value;
                        _sum += value;
                    }
                    else
                    {
                        _sum -= _samples[_index];
                        _samples[_index] = value;
                        _sum += value;
                        _index = (_index + 1) % _samples.Length;
                    }
                }
            }
        }
    }
}
