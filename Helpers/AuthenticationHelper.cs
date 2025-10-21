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
    }
}