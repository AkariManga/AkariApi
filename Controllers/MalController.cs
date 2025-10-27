using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Attributes;
using System.Text.Json;
using AkariApi.Helpers;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/mal")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class MalController : ControllerBase
    {
        private readonly string clientId;

        public MalController(IConfiguration configuration)
        {
            clientId = configuration["MAL_CLIENT_ID"] ?? string.Empty;
        }

        /// <summary>
        /// Exchange authorization code for access token
        /// </summary>
        /// <param name="request">The token request containing code and code_verifier.</param>
        /// <returns>The token response with access and refresh tokens.</returns>
        [HttpPost("token")]
        [ProducesResponseType(typeof(SuccessResponse<MalTokenResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> TokenExchange([FromBody] MalTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
            {
                return BadRequest(ErrorResponse.Create("Missing input"));
            }

            if (string.IsNullOrEmpty(clientId))
            {
                return StatusCode(500, ErrorResponse.Create("Configuration error"));
            }

            using var httpClient = new HttpClient();
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("code", request.Code),
                new KeyValuePair<string, string>("code_verifier", request.CodeVerifier),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("redirect_uri", request.RedirectUri),
            });

            var response = await httpClient.PostAsync("https://myanimelist.net/v1/oauth2/token", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<MalTokenResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from MAL"));
                }
                var now = DateTime.UtcNow;

                CookieHelper.SetCookie(Response, "mal_access_token", data.AccessToken, expires: TimeSpan.FromSeconds(data.ExpiresIn));
                CookieHelper.SetCookie(Response, "mal_refresh_token", data.RefreshToken, expires: TimeSpan.FromDays(31));

                return Ok(SuccessResponse<MalTokenResponse>.Create(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Unknown error" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message));
            }
        }

        /// <summary>
        /// Logout by clearing tokens
        /// </summary>
        /// <returns>Success response</returns>
        [HttpPost("logout")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("mal_access_token");
            Response.Cookies.Delete("mal_refresh_token");
            return Ok(SuccessResponse<string>.Create("Logged out"));
        }

        /// <summary>
        /// Update user's manga list status
        /// </summary>
        /// <param name="request">The update request containing manga_id and num_chapters_read.</param>
        /// <returns>The updated manga list status.</returns>
        [HttpPost("mangalist")]
        [RequireMalTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<MalMangaListStatus>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateMangaList([FromBody] MalUpdateMangaListRequest request)
        {
            if (request.MangaId <= 0 || request.NumChaptersRead < 0)
            {
                return BadRequest(ErrorResponse.Create("Invalid input"));
            }

            var accessToken = Request.Cookies["mal_access_token"];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ErrorResponse.Create("Missing access token"));
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("num_chapters_read", request.NumChaptersRead.ToString()),
            });

            var response = await httpClient.PatchAsync($"https://api.myanimelist.net/v2/manga/{request.MangaId}/my_list_status", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<MalMangaListStatus>(responseContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true });
                if (data == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from MAL"));
                }
                return Ok(SuccessResponse<MalMangaListStatus>.Create(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to update manga list" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message));
            }
        }

        /// <summary>
        /// Get user's manga list
        /// </summary>
        /// <param name="status">Filters returned manga list by status.</param>
        /// <param name="sort">Sort order for the list.</param>
        /// <param name="limit">Maximum number of items to return (default 100, max 1000).</param>
        /// <param name="offset">Offset for pagination (default 0).</param>
        /// <returns>The user's manga list.</returns>
        [HttpGet("mangalist")]
        [RequireMalTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<MalMangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaList([FromQuery] string? status, [FromQuery] string? sort, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
        {
            if (limit < 1 || limit > 1000)
            {
                return BadRequest(ErrorResponse.Create("Limit must be between 1 and 1000"));
            }

            if (offset < 0)
            {
                return BadRequest(ErrorResponse.Create("Offset must be non-negative"));
            }

            var accessToken = Request.Cookies["mal_access_token"];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ErrorResponse.Create("Missing access token"));
            }

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(status))
            {
                queryParams.Add($"status={Uri.EscapeDataString(status)}");
            }
            if (!string.IsNullOrEmpty(sort))
            {
                queryParams.Add($"sort={Uri.EscapeDataString(sort)}");
            }
            queryParams.Add($"limit={limit}");
            queryParams.Add($"offset={offset}");
            queryParams.Add("sort=list_score");
            queryParams.Add("fields=list_status");

            var queryString = string.Join("&", queryParams);
            var url = $"https://api.myanimelist.net/v2/users/@me/mangalist?{queryString}";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(url);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<MalMangaListResponse>(responseContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true });
                if (data == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from MAL"));
                }
                return Ok(SuccessResponse<MalMangaListResponse>.Create(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to get manga list" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message));
            }
        }
    }
}