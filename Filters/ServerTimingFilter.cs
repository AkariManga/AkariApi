using Lib.ServerTiming;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Diagnostics;

namespace AkariApi.Filters;

/// <summary>
/// Global action filter that automatically records the action execution duration
/// as a <c>Server-Timing</c> metric for every controller endpoint, requiring no
/// per-endpoint code.
/// </summary>
public class ServerTimingFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var serverTiming = context.HttpContext.RequestServices.GetService<IServerTiming>();
        var sw = Stopwatch.StartNew();

        await next();

        sw.Stop();
        serverTiming?.AddMetric((decimal)sw.Elapsed.TotalMilliseconds, "action");
    }
}
