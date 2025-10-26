using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/mal")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class MalController : ControllerBase
    {
        private readonly string clientId;

        public MalController()
        {
            clientId = Environment.GetEnvironmentVariable("CLIENT_ID") ?? string.Empty;
        }

        /// <summary>
        /// Exchange authorization code for access token
        /// </summary>
        /// <param name="request">The token request containing code and code_verifier.</param>
        /// <returns>The token response with access and refresh tokens.</returns>
        [HttpPost("token")]
        [ProducesResponseType(typeof(ApiResponse<MalTokenResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> TokenExchange([FromBody] MalTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Missing input"));
            }

            if (string.IsNullOrEmpty(clientId))
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Configuration error"));
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
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Invalid response from MAL"));
                }
                var now = DateTime.UtcNow;
                var accessExpiration = now.AddSeconds(data.ExpiresIn);
                var refreshExpiration = now.AddDays(31);

                Response.Cookies.Append("mal_access_token", data.AccessToken, new CookieOptions
                {
                    HttpOnly = true,
                    Expires = accessExpiration,
                    Path = "/"
                });
                Response.Cookies.Append("mal_refresh_token", data.RefreshToken, new CookieOptions
                {
                    HttpOnly = true,
                    Expires = refreshExpiration,
                    Path = "/"
                });

                return Ok(ApiResponse<MalTokenResponse>.Success(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Unknown error" };
                return StatusCode((int)response.StatusCode, ApiResponse<ErrorData>.Error(errorData.Message));
            }
        }

        /// <summary>
        /// Logout by clearing tokens
        /// </summary>
        /// <returns>Success response</returns>
        [HttpPost("logout")]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("mal_access_token");
            Response.Cookies.Delete("mal_refresh_token");
            return Ok(ApiResponse<string>.Success("Logged out"));
        }

        /// <summary>
        /// Update user's manga list status
        /// </summary>
        /// <param name="request">The update request containing manga_id and num_chapters_read.</param>
        /// <returns>The updated manga list status.</returns>
        [HttpPost("mangalist")]
        [RequireMalTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<MalMangaListStatus>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> UpdateMangaList([FromBody] MalUpdateMangaListRequest request)
        {
            if (request.MangaId <= 0 || request.NumChaptersRead < 0)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Invalid input"));
            }

            var accessToken = Request.Cookies["mal_access_token"];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Missing access token"));
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
                var data = JsonSerializer.Deserialize<MalMangaListStatus>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data == null)
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Invalid response from MAL"));
                }
                return Ok(ApiResponse<MalMangaListStatus>.Success(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to update manga list" };
                return StatusCode((int)response.StatusCode, ApiResponse<ErrorData>.Error(errorData.Message));
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
        [ProducesResponseType(typeof(ApiResponse<MalMangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetMangaList([FromQuery] string? status, [FromQuery] string? sort, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
        {
            if (limit < 1 || limit > 1000)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Limit must be between 1 and 1000"));
            }

            if (offset < 0)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Offset must be non-negative"));
            }

            var accessToken = Request.Cookies["mal_access_token"];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Missing access token"));
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

            var queryString = string.Join("&", queryParams);
            var url = $"https://api.myanimelist.net/v2/users/@me/mangalist?{queryString}";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(url);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<MalMangaListResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data == null)
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Invalid response from MAL"));
                }
                return Ok(ApiResponse<MalMangaListResponse>.Success(data));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to get manga list" };
                return StatusCode((int)response.StatusCode, ApiResponse<ErrorData>.Error(errorData.Message));
            }
        }
    }
}