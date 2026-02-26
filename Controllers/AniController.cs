using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Attributes;
using System.Text.Json;
using System.Net.Http.Headers;
using AkariApi.Helpers;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/ani")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    [DisableAnalytics]
    public class AniController : ControllerBase
    {
        private const string AniListApiUrl = "https://graphql.anilist.co";
        private const string AniListCookieName = "ani_access_token";

        /// <summary>
        /// Get current user info from AniList
        /// </summary>
        /// <returns>The current user's AniList info.</returns>
        [HttpGet("me")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<AniViewer>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> Me([FromQuery] string? access_token = null, [FromQuery] int? expires_in = null)
        {
            var accessToken = access_token ?? Request.Cookies[AniListCookieName];
            var expiresIn = expires_in ?? 0;

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ErrorResponse.Create("Missing access token", status: 401));
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var query = @"query {
  Viewer {
    id
    name
  }
}";
            var requestBody = new { query };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(AniListApiUrl, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                if (expiresIn > 0)
                {
                    CookieHelper.SetCookie(Response, AniListCookieName, accessToken, expires: TimeSpan.FromSeconds(expiresIn));
                }

                var data = JsonSerializer.Deserialize<AniUserResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data?.Data?.Viewer == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from AniList"));
                }
                return Ok(SuccessResponse<AniViewer>.Create(data.Data.Viewer));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to get user info" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message, status: (int)response.StatusCode));
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
            CookieHelper.DeleteCookie(Response, AniListCookieName);
            return Ok(SuccessResponse<string>.Create("Logged out"));
        }

        /// <summary>
        /// Get user's manga list from AniList
        /// </summary>
        /// <param name="userName">The AniList username</param>
        /// <returns>The user's manga list</returns>
        [HttpGet("mangalist")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<AniMediaListCollection>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaList([FromQuery] string userName)
        {
            var accessToken = Request.Cookies[AniListCookieName];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ErrorResponse.Create("Missing access token", status: 401));
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var query = @"query GetUserMangaList($userName: String, $type: MediaType = MANGA) {
  MediaListCollection(userName: $userName, type: $type) {
    lists {
      name
      entries {
        id
        score
        progress
        status
        media {
          id
          title { english }
        }
      }
    }
  }
}";
            var variables = new { userName, type = "MANGA" };
            var requestBody = new { query, variables };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(AniListApiUrl, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<AniMangaListResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data?.Data?.MediaListCollection == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from AniList"));
                }
                return Ok(SuccessResponse<AniMediaListCollection>.Create(data.Data.MediaListCollection));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to get manga list" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message, status: (int)response.StatusCode));
            }
        }

        /// <summary>
        /// Update user's manga list on AniList
        /// </summary>
        /// <param name="request">The update request containing mediaId and progress.</param>
        /// <returns>The updated manga list entry.</returns>
        [HttpPost("mangalist")]
        [ProducesResponseType(typeof(SuccessResponse<AniUpdatedEntry>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateMangaList([FromBody] AniUpdateMangaListRequest request)
        {
            if (request.MediaId <= 0 || request.Progress < 0)
            {
                return BadRequest(ErrorResponse.Create("Invalid input", status: 400));
            }

            var accessToken = Request.Cookies[AniListCookieName];

            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ErrorResponse.Create("Missing access token", status: 401));
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var query = @"mutation SaveMangaListEntry(
  $mediaId: Int!,
  $status: MediaListStatus!,
  $progress: Int,
) {
  SaveMediaListEntry(
    mediaId: $mediaId,
    status: $status,
    progress: $progress,
  ) {
    id
    status
    progress
  }
}";
            var variables = new { mediaId = request.MediaId, status = "CURRENT", progress = request.Progress };
            var requestBody = new { query, variables };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(AniListApiUrl, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<AniUpdateResponse>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data?.Data?.SaveMediaListEntry == null)
                {
                    return StatusCode(500, ErrorResponse.Create("Invalid response from AniList"));
                }
                return Ok(SuccessResponse<AniUpdatedEntry>.Create(data.Data.SaveMediaListEntry));
            }
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ErrorData { Message = "Failed to update manga list" };
                return StatusCode((int)response.StatusCode, ErrorResponse.Create(errorData.Message, status: (int)response.StatusCode));
            }
        }
    }
}
