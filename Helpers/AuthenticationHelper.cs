using AkariApi.Services;

namespace AkariApi.Helpers
{
    public static class AuthenticationHelper
    {
        public static string GetAccessToken(HttpRequest request)
        {
            var refreshedToken = request.HttpContext.Items["RefreshedAccessToken"] as string;
            if (!string.IsNullOrEmpty(refreshedToken))
            {
                return refreshedToken;
            }

            // Fallback to original logic
            var token = request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(token) && token.StartsWith("Bearer "))
            {
                return token.Substring("Bearer ".Length);
            }
            else
            {
                return request.Cookies["accessToken"] ?? string.Empty;
            }
        }

        public static string GetRefreshToken(HttpRequest request)
        {
            var refreshedToken = request.HttpContext.Items["RefreshedRefreshToken"] as string;
            if (!string.IsNullOrEmpty(refreshedToken))
            {
                return refreshedToken;
            }

            return request.Cookies["refreshToken"] ?? string.Empty;
        }

        public static async Task<(Guid userId, string errorMessage)> AuthenticateAndSetSessionAsync(HttpRequest request, SupabaseService supabaseService)
        {
            var accessToken = GetAccessToken(request);
            if (string.IsNullOrEmpty(accessToken))
            {
                return (Guid.Empty, "Access token required");
            }

            var user = await supabaseService.Client.Auth.GetUser(accessToken);
            if (user == null || string.IsNullOrEmpty(user.Id))
            {
                return (Guid.Empty, "Invalid access token");
            }

            var refreshToken = GetRefreshToken(request);
            if (string.IsNullOrEmpty(refreshToken))
            {
                return (Guid.Empty, "Refresh token required");
            }

            await supabaseService.Client.Auth.SetSession(accessToken, refreshToken);

            if (!Guid.TryParse(user.Id, out var userId))
            {
                return (Guid.Empty, "Invalid user ID format");
            }

            return (userId, string.Empty);
        }
    }
}