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
    }
}