using Microsoft.AspNetCore.Mvc.Filters;

namespace AkariApi.Attributes
{
    public class CacheControlAttribute : ActionFilterAttribute
    {
        private readonly int _maxAge;
        private readonly int _staleWhileRevalidate;
        private readonly bool _isPublic;

        public CacheControlAttribute(int maxAge, int staleWhileRevalidate = 0, bool isPublic = true)
        {
            _maxAge = maxAge;
            _staleWhileRevalidate = staleWhileRevalidate;
            _isPublic = isPublic;
        }

        public override void OnActionExecuted(ActionExecutedContext context)
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