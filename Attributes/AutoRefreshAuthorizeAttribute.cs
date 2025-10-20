using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using AkariApi.Services;
using System.IdentityModel.Tokens.Jwt;
using AkariApi.Helpers;

namespace AkariApi.Attributes
{
    public class AutoRefreshAuthorizeAttribute : ActionFilterAttribute
    {
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var _supabaseService = context.HttpContext.RequestServices.GetRequiredService<SupabaseService>();
            var request = context.HttpContext.Request;
            var response = context.HttpContext.Response;

            // Extract access token from cookie
            var token = AuthenticationHelper.GetAccessToken(request);
            if (string.IsNullOrEmpty(token))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);

            // Check if token is expired
            if (jwtToken.ValidTo < DateTime.UtcNow.AddMinutes(1)) // Refresh if expiring soon
            {
                // Extract refresh token from cookie
                var refreshToken = request.Cookies["refreshToken"];
                if (string.IsNullOrEmpty(refreshToken))
                {
                    context.Result = new UnauthorizedResult();
                    return;
                }

                try
                {
                    await _supabaseService.InitializeAsync();
                    var newSession = await _supabaseService.Client.Auth.RefreshSession();

                    if (newSession == null || string.IsNullOrEmpty(newSession.AccessToken) || string.IsNullOrEmpty(newSession.RefreshToken))
                    {
                        context.Result = new UnauthorizedResult();
                        return;
                    }

                    // Update cookies with new tokens
                    response.Cookies.Append("accessToken", newSession.AccessToken, new CookieOptions { HttpOnly = true, Secure = true });
                    response.Cookies.Append("expiresIn", newSession.ExpiresIn.ToString(), new CookieOptions { HttpOnly = true, Secure = true });
                    response.Cookies.Append("refreshToken", newSession.RefreshToken, new CookieOptions { HttpOnly = true, Secure = true });
                }
                catch
                {
                    context.Result = new UnauthorizedResult();
                    return;
                }
            }

            await next();
        }
    }
}
