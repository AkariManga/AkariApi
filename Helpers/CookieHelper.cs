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
            var options = new CookieOptions
            {
                HttpOnly = !isDevelopment,
                Secure = !isDevelopment,
                SameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.Strict,
                Path = path,
                Domain = isDevelopment ? "localhost" : "api.akarimanga.dpdns.org",
                Expires = expires.HasValue ? DateTimeOffset.UtcNow.Add(expires.Value) : (DateTimeOffset?)null
            };
            response.Cookies.Append(name, value, options);
        }
    }
}