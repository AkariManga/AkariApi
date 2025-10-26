using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Text.Json;
using AkariApi.Attributes;
using AkariApi.Models;

namespace AkariApi.Middleware
{
    public class MalTokenRefreshMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _clientId;

        public MalTokenRefreshMiddleware(RequestDelegate next)
        {
            _next = next;
            _clientId = Environment.GetEnvironmentVariable("CLIENT_ID") ?? string.Empty;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var endpoint = context.GetEndpoint();
            var requireRefresh = endpoint?.Metadata.GetMetadata<RequireMalTokenRefreshAttribute>() != null;

            if (!requireRefresh)
            {
                await _next(context);
                return;
            }

            var accessToken = context.Request.Cookies["mal_access_token"];
            var refreshToken = context.Request.Cookies["mal_refresh_token"];
            bool needsRefresh = false;

            if (!string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwtToken = handler.ReadJwtToken(accessToken);
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

            if (needsRefresh && !string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    using var httpClient = new HttpClient();
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("client_id", _clientId),
                        new KeyValuePair<string, string>("refresh_token", refreshToken),
                        new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    });

                    var response = await httpClient.PostAsync("https://myanimelist.net/v1/oauth2/token", content);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var data = JsonSerializer.Deserialize<MalTokenResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (data != null)
                        {
                            var now = DateTime.UtcNow;
                            var accessExpiration = now.AddSeconds(data.ExpiresIn);
                            var refreshExpiration = now.AddDays(31);

                            context.Response.Cookies.Append("mal_access_token", data.AccessToken, new CookieOptions
                            {
                                HttpOnly = true,
                                Expires = accessExpiration,
                                Path = "/"
                            });
                            context.Response.Cookies.Append("mal_refresh_token", data.RefreshToken, new CookieOptions
                            {
                                HttpOnly = true,
                                Expires = refreshExpiration,
                                Path = "/"
                            });
                        }
                        else
                        {
                            context.Response.Cookies.Delete("mal_access_token");
                            context.Response.Cookies.Delete("mal_refresh_token");
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Invalid refresh response", 401);
                            await context.Response.WriteAsJsonAsync(errorResponse);
                            return;
                        }
                    }
                    else
                    {
                        context.Response.Cookies.Delete("mal_access_token");
                        context.Response.Cookies.Delete("mal_refresh_token");
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json";
                        var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed", 401);
                        await context.Response.WriteAsJsonAsync(errorResponse);
                        return;
                    }
                }
                catch
                {
                    context.Response.Cookies.Delete("mal_access_token");
                    context.Response.Cookies.Delete("mal_refresh_token");
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    var errorResponse = ApiResponse<ErrorData>.Error("Unauthorized", "Token refresh failed", 401);
                    await context.Response.WriteAsJsonAsync(errorResponse);
                    return;
                }
            }
            else if (needsRefresh)
            {
                context.Response.Cookies.Delete("mal_access_token");
                context.Response.Cookies.Delete("mal_refresh_token");
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