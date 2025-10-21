using AkariApi.Services;
using System.IdentityModel.Tokens.Jwt;
using AkariApi.Helpers;
using AkariApi.Models;
using AkariApi.Attributes;
using Microsoft.Extensions.Logging;

namespace AkariApi.Middleware
{
    public class TokenRefreshMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TokenRefreshMiddleware> _logger;

        public TokenRefreshMiddleware(RequestDelegate next, ILogger<TokenRefreshMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, SupabaseService supabaseService)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<RequireTokenRefreshAttribute>() == null)
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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse JWT token for request {Path}: {Message}", context.Request.Path, ex.Message);
                    needsRefresh = true;
                }
            }
            else
            {
                needsRefresh = !string.IsNullOrEmpty(refreshToken);
            }

            if (needsRefresh && !string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    await supabaseService.InitializeAsync();
                    await supabaseService.Client.Auth.SetSession(token, refreshToken);
                    var newSession = await supabaseService.Client.Auth.RefreshSession();

                    if (newSession != null && !string.IsNullOrEmpty(newSession.AccessToken) && !string.IsNullOrEmpty(newSession.RefreshToken))
                    {
                        context.Items["RefreshedAccessToken"] = newSession.AccessToken;
                        context.Response.Cookies.Append("accessToken", newSession.AccessToken, new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = true,
                            SameSite = SameSiteMode.Strict,
                            Expires = DateTimeOffset.UtcNow.AddSeconds(newSession.ExpiresIn)
                        });
                        context.Response.Cookies.Append("refreshToken", newSession.RefreshToken, new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = true,
                            SameSite = SameSiteMode.Strict,
                            Expires = DateTimeOffset.UtcNow.AddDays(365)
                        });
                    }
                    else
                    {
                        _logger.LogWarning("Token refresh returned invalid session (null or missing access token) for request {Path}", context.Request.Path);
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json";
                        var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Invalid refresh token or session expired", 401);
                        await context.Response.WriteAsJsonAsync(errorResponse);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Token refresh failed for request {Path}: {Message}", context.Request.Path, ex.Message);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed due to an internal error", 401);
                    await context.Response.WriteAsJsonAsync(errorResponse);
                    return;
                }
            }
            else if (needsRefresh && string.IsNullOrEmpty(refreshToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Missing refresh token", 401);
                await context.Response.WriteAsJsonAsync(errorResponse);
                return;
            }

            await _next(context);
        }
    }
}