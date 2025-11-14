namespace AkariApi.Helpers
{
    public static class CookieHelper
    {
        /// <summary>
        /// Sets a cookie with options that are automatically adjusted based on the environment.
        /// In development, HttpOnly is set to false for easier debugging, Secure is set to false to allow HTTP,
        /// and SameSite is set to Lax for compatibility.
        /// </summary>
        /// <param name="response">The HTTP response to append the cookie to.</param>
        /// <param name="name">The name of the cookie.</param>
        /// <param name="value">The value of the cookie.</param>
        /// <param name="expires">Optional expiration time span from now.</param>
        /// <param name="path">The path for the cookie (default "/").</param>
        public static void SetCookie(HttpResponse response, string name, string value, TimeSpan? expires = null, string path = "/")
        {
            bool isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            var request = response.HttpContext.Request;
            var options = new CookieOptions
            {
                HttpOnly = !isDevelopment,
                Secure = !isDevelopment,
                SameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.None,
                Path = path,
                Domain = GetCookieDomain(request.Host.Host),
                Expires = expires.HasValue ? DateTimeOffset.UtcNow.Add(expires.Value) : (DateTimeOffset?)null
            };
            response.Cookies.Append(name, value, options);
        }

        private static string? GetCookieDomain(string host)
        {
            if (string.IsNullOrEmpty(host))
                return null;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return null;
            if (System.Net.IPAddress.TryParse(host, out _))
                return null;

            var suffix = GetPublicSuffix(host);
            if (suffix == null)
                return null;
            var root = host.Substring(0, host.Length - suffix.Length - 1);
            return "." + root;
        }

        private static string? GetPublicSuffix(string domain)
        {
            var labels = domain.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                var suffix = string.Join(".", labels.Skip(i));
                if (IsPublicSuffix(suffix))
                    return suffix;
            }
            return null;
        }

        private static bool IsPublicSuffix(string s)
        {
            if (PublicSuffixData.Exceptions.Contains(s))
                return false;
            if (PublicSuffixData.Exact.Contains(s))
                return true;
            var parts = s.Split('.');
            if (parts.Length > 1 && PublicSuffixData.Wildcards.Contains(parts[^1]))
                return true;
            return false;
        }
    }
}