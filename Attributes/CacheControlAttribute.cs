using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AkariApi.Attributes
{
    public enum CacheDuration
    {
        NoCache = 0,
        ThirtySeconds = 30,
        OneMinute = 60,
        FiveMinutes = 300,
        TenMinutes = 600,
        ThirtyMinutes = 1800,
        OneHour = 3600,
        SixHours = 21600,
        TwelveHours = 43200,
        OneDay = 86400,
        SevenDays = 604800
    }

    public class CacheControlAttribute : ActionFilterAttribute
    {
        private readonly int _maxAge;
        private readonly int _staleWhileRevalidate;
        private readonly bool _isPublic;

        public CacheControlAttribute(CacheDuration maxAge, CacheDuration staleWhileRevalidate = CacheDuration.NoCache, bool isPublic = true)
        {
            _maxAge = (int)maxAge;
            _staleWhileRevalidate = (int)staleWhileRevalidate;
            _isPublic = isPublic;
        }

        public override void OnActionExecuted(ActionExecutedContext context)
        {
            // determine the status code that will be sent. 200 is the default for
            // Ok()/ObjectResult; other results may override it.
            int statusCode = 200;
            if (context.Result is StatusCodeResult statusResult)
            {
                statusCode = statusResult.StatusCode;
            }
            else if (context.Result is ObjectResult objectResult && objectResult.StatusCode.HasValue)
            {
                statusCode = objectResult.StatusCode.Value;
            }

            // any non-200 status is not cached by clients or proxies; early exit
            if (statusCode != 200)
            {
                context.HttpContext.Response.Headers.CacheControl =
                    "no-store, no-cache, must-revalidate";
                return;
            }

            // at this point we're handling a successful 200 response
            // try to generate an ETag; the method returns null when the result type
            // isn't supported or the value itself is null.
            var etag = GenerateEtag(context);
            if (!string.IsNullOrEmpty(etag))
            {
                var response = context.HttpContext.Response;

                response.Headers.ETag = etag;
                // some proxies vary on encoding, so make sure clients revalidate when
                // the encoding changes.
                response.Headers.Append("Vary", "Accept-Encoding");

                // handle conditional request from the client
                var request = context.HttpContext.Request;
                if (request.Headers.TryGetValue("If-None-Match", out var incoming))
                {
                    var received = incoming
                        .SelectMany(h => h?.Split(',') ?? Array.Empty<string>())
                        .Select(v => v.Trim())
                        .Where(v => !string.IsNullOrEmpty(v));

                    if (received.Contains(etag))
                    {
                        // nothing changed; return 304 without a body.
                        context.Result = new StatusCodeResult(304);
                        statusCode = 304; // fall through so cache header is still written
                    }
                }
            }

            // always include a last‑modified timestamp for clients/intermediaries
            context.HttpContext.Response.Headers.LastModified =
                DateTime.UtcNow.ToString("R");

            // compute cache-control value for OK responses (including any 304 we
            // just short‑circuited to).  Avoid repeating the check elsewhere.
            var cacheType = _isPublic ? "public" : "private";
            var cacheControl = $"{cacheType}, max-age={_maxAge}";
            if (_staleWhileRevalidate > 0)
            {
                cacheControl += $", stale-while-revalidate={_staleWhileRevalidate}";
            }

            context.HttpContext.Response.Headers.CacheControl = cacheControl;
        }

        private static readonly JsonSerializerOptions s_camelCaseOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>
        /// Attempts to create a stable ETag value from the result object.
        /// Returns <c>null</c> if the result type is not supported or the value is <c>null</c>.
        /// </summary>
        // static cached serializer options to avoid allocating on every request.
        private static string? GenerateEtag(ActionExecutedContext context)
        {
            byte[]? raw = null;

            switch (context.Result)
            {
                case ObjectResult obj when obj.Value != null:
                    try
                    {
                        var json = JsonSerializer.Serialize(obj.Value, s_camelCaseOptions);
                        raw = Encoding.UTF8.GetBytes(json);
                    }
                    catch { /* fall through to null */ }
                    break;
                case JsonResult json when json.Value != null:
                    try
                    {
                        var jsonText = JsonSerializer.Serialize(json.Value, s_camelCaseOptions);
                        raw = Encoding.UTF8.GetBytes(jsonText);
                    }
                    catch { }
                    break;
                case ContentResult content when content.Content != null:
                    raw = Encoding.UTF8.GetBytes(content.Content);
                    break;
                case FileContentResult file:
                    raw = file.FileContents;
                    break;
                case EmptyResult _:
                    raw = Array.Empty<byte>();
                    break;
                    // other result types (stream, file stream, etc.) are not hashed here
            }

            if (raw == null)
                return null;
            var hash = SHA256.HashData(raw);
            var base64 = Convert.ToBase64String(hash);
            return "\"" + base64 + "\""; // quoted string per RFC
        }
    }
}