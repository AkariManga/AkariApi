using AkariApi.Services;
using Dapper;
using Lib.ServerTiming;
using Supabase.Gotrue.Exceptions;

namespace AkariApi.Helpers
{
    public static class AuthenticationHelper
    {
        public static string GetAccessToken(HttpRequest request)
        {
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

            var serverTiming = request.HttpContext.RequestServices.GetService<IServerTiming>();

            try
            {
                var getUserTask = supabaseService.Client.Auth.GetUser(accessToken);
                var user = serverTiming != null
                    ? await serverTiming.TimeTask(getUserTask, "auth")
                    : await getUserTask;

                if (user == null || string.IsNullOrEmpty(user.Id))
                {
                    return (Guid.Empty, "Invalid access token");
                }

                if (!Guid.TryParse(user.Id, out var userId))
                {
                    return (Guid.Empty, "Invalid user ID format");
                }

                return (userId, string.Empty);
            }
            catch (GotrueException)
            {
                return (Guid.Empty, "Invalid or expired token");
            }
        }

        public static async Task<bool> IsUserBannedAsync(Guid userId, PostgresService postgresService)
        {
            const string query = "SELECT banned FROM public.profiles WHERE id = @userId";
            try
            {
                var result = await postgresService.Connection.ExecuteScalarAsync<bool?>(query, new { userId });
                return result ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}