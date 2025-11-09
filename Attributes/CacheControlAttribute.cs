using Microsoft.AspNetCore.Mvc.Filters;

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

    public class CacheControlAttribute : ResultFilterAttribute
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

        public override void OnResultExecuted(ResultExecutedContext context)
        {
            if (context.HttpContext.Response.StatusCode == 200)
            {
                var cacheType = _isPublic ? "public" : "private";
                var cacheControl = $"{cacheType}, max-age={_maxAge}";
                if (_staleWhileRevalidate > 0)
                {
                    cacheControl += $", stale-while-revalidate={_staleWhileRevalidate}";
                }
                context.HttpContext.Response.Headers["Cache-Control"] = cacheControl;
            }
            else
            {
                context.HttpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            }
        }
    }
}