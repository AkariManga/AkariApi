using AkariApi.Services;
using System.IdentityModel.Tokens.Jwt;
using AkariApi.Helpers;
using AkariApi.Models;
using AkariApi.Attributes;

namespace AkariApi.Middleware
{
    public class TokenRefreshMiddleware
    {
        private readonly RequestDelegate _next;

        public TokenRefreshMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, SupabaseService supabaseService)
        {
            var endpoint = context.GetEndpoint();
            var requireRefresh = endpoint?.Metadata.GetMetadata<RequireTokenRefreshAttribute>() != null;
            var optionalRefresh = endpoint?.Metadata.GetMetadata<OptionalTokenRefreshAttribute>() != null;

            if (!requireRefresh && !optionalRefresh)
            {
                await _next(context);
                return;
            }

            var token = AuthenticationHelper.GetAccessToken(context.Request);
            var refreshToken = context.Request.Cookies["refreshToken"];
            bool needsRefresh = false;

            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwtToken = handler.ReadJwtToken(token);
                    if (jwtToken.ValidTo < DateTime.UtcNow.AddMinutes(5))
                    {
                        needsRefresh = true;
                    }
                }
                catch
                {
                    needsRefresh = true;
                }
            }

            if (needsRefresh && !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(token))
            {
                try
                {
                    await supabaseService.InitializeAsync();
                    await supabaseService.Client.Auth.SetSession(token, refreshToken);
                    await supabaseService.Client.Auth.RefreshToken();

                    var newSession = supabaseService.Client.Auth.CurrentSession;

                    if (newSession != null && !string.IsNullOrEmpty(newSession.AccessToken) && !string.IsNullOrEmpty(newSession.RefreshToken))
                    {
                        context.Items["RefreshedAccessToken"] = newSession.AccessToken;
                        context.Items["RefreshedRefreshToken"] = newSession.RefreshToken;
                        CookieHelper.SetCookie(context.Response, "accessToken", newSession.AccessToken, expires: TimeSpan.FromDays(365));
                        CookieHelper.SetCookie(context.Response, "refreshToken", newSession.RefreshToken, expires: TimeSpan.FromDays(365));
                    }
                    else
                    {
                        if (requireRefresh)
                        {
                            context.Response.Cookies.Delete("accessToken");
                            context.Response.Cookies.Delete("refreshToken");
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Invalid refresh token or session expired", 401);
                            await context.Response.WriteAsJsonAsync(errorResponse);
                            return;
                        }
                    }
                }
                catch
                {
                    if (requireRefresh)
                    {
                        context.Response.Cookies.Delete("accessToken");
                        context.Response.Cookies.Delete("refreshToken");
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json";
                        var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed - please sign in again", 401);
                        await context.Response.WriteAsJsonAsync(errorResponse);
                        return;
                    }
                }
            }
            else if (needsRefresh && requireRefresh)
            {
                context.Response.Cookies.Delete("accessToken");
                context.Response.Cookies.Delete("refreshToken");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Re-authentication required", 401);
                await context.Response.WriteAsJsonAsync(errorResponse);
                return;
            }

            await _next(context);
        }
    }
}