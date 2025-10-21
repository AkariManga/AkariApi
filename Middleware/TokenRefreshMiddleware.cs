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
            if (endpoint?.Metadata.GetMetadata<RequireTokenRefreshAttribute>() == null)
            {
                await _next(context);
                return;
            }

            var token = AuthenticationHelper.GetAccessToken(context.Request);
            if (!string.IsNullOrEmpty(token))
            {
                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(token);

                if (jwtToken.ValidTo < DateTime.UtcNow.AddMinutes(5))
                {
                    var refreshToken = context.Request.Cookies["refreshToken"];
                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        try
                        {
                            await supabaseService.InitializeAsync();
                            var newSession = await supabaseService.Client.Auth.RefreshSession();

                            if (newSession != null && !string.IsNullOrEmpty(newSession.AccessToken))
                            {
                                context.Items["RefreshedAccessToken"] = newSession.AccessToken;
                                context.Response.Cookies.Append("accessToken", newSession.AccessToken, new CookieOptions
                                {
                                    HttpOnly = true,
                                    Secure = true,
                                    SameSite = SameSiteMode.Strict,
                                    Expires = DateTimeOffset.UtcNow.AddSeconds(newSession.ExpiresIn)
                                });
                                context.Response.Cookies.Append("refreshToken", newSession.RefreshToken ?? refreshToken, new CookieOptions
                                {
                                    HttpOnly = true,
                                    Secure = true,
                                    SameSite = SameSiteMode.Strict,
                                    Expires = DateTimeOffset.UtcNow.AddDays(365)
                                });
                            }
                            else
                            {
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                context.Response.ContentType = "application/json";
                                var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed", 401);
                                await context.Response.WriteAsJsonAsync(errorResponse);
                                return;
                            }
                        }
                        catch
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed", 401);
                            await context.Response.WriteAsJsonAsync(errorResponse);
                            return;
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json";
                        var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Missing refresh token", 401);
                        await context.Response.WriteAsJsonAsync(errorResponse);
                        return;
                    }
                }
                else
                {
                    context.Items["RefreshedAccessToken"] = token;
                }
            }

            await _next(context);
        }
    }
}