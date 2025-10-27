using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/manga")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class MangaController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public MangaController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        /// <summary>
        /// Get manga list
        /// </summary>
        /// <param name="sortBy">The sort order: 'popular', 'latest', 'newest'.</param>
        /// <param name="query">Search query string.</param>
        /// <param name="genres">Filter by genres.</param>
        /// <param name="authors">Filter by authors.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of manga.</returns>
        [HttpGet("list")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaList(
            [FromQuery] string sortBy = "latest",
            [FromQuery] string query = "",
            [FromQuery] string[]? genres = null,
            [FromQuery] string[]? authors = null,
            [FromQuery, Range(1, int.MaxValue)] int page = 1,
            [FromQuery, Range(1, 100)] int pageSize = 20
        )
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _supabaseService.InitializeAsync();

                var rpcResponse = await _supabaseService.Client.Rpc("get_manga", new
                {
                    p_limit = clampedPageSize,
                    p_offset = offset,
                    p_sort_by = sortBy,
                    p_genres = genres,
                    p_authors = authors,
                    p_query = query,
                });

                if (string.IsNullOrEmpty(rpcResponse.Content))
                {
                    return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                    {
                        Items = new List<MangaResponse>(),
                        TotalItems = 0,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var rpcResults = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(rpcResponse.Content, _jsonOptions);

                var mangaList = new List<MangaResponse>();
                long totalCount = 0;

                foreach (var item in rpcResults ?? new List<Dictionary<string, JsonElement>>())
                {
                    if (item.TryGetValue("id", out var idElement) && idElement.ValueKind == JsonValueKind.String && Guid.TryParse(idElement.GetString(), out var id))
                    {
                        var manga = new MangaResponse
                        {
                            Id = id,
                            Title = item.TryGetValue("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? "" : "",
                            Cover = item.TryGetValue("cover", out var cover) && cover.ValueKind == JsonValueKind.String ? cover.GetString() ?? "" : "",
                            Description = item.TryGetValue("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? "" : "",
                            Status = item.TryGetValue("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() ?? "" : "",
                            Type = item.TryGetValue("type", out var type) && type.ValueKind == JsonValueKind.String && Enum.TryParse<MangaType>(type.GetString(), out var mangaType) ? mangaType : MangaType.Manga,
                            Authors = item.TryGetValue("authors", out var authorsArr) && authorsArr.ValueKind == JsonValueKind.Array ? authorsArr.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
                            Genres = item.TryGetValue("genres", out var genresArr) && genresArr.ValueKind == JsonValueKind.Array ? genresArr.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
                            Views = item.TryGetValue("view_count", out var views) && views.ValueKind == JsonValueKind.Number && views.TryGetInt64(out var viewCount) ? (int)viewCount : 0,
                            Score = item.TryGetValue("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDecimal(out var scoreVal) ? scoreVal : 0,
                            AlternativeTitles = item.TryGetValue("alternative_titles", out var altTitles) && altTitles.ValueKind == JsonValueKind.Array ? altTitles.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null,
                            MalId = item.TryGetValue("mal_id", out var malId) && malId.ValueKind == JsonValueKind.Number && malId.TryGetInt32(out var malIdVal) ? malIdVal : null,
                            AniId = item.TryGetValue("ani_id", out var aniId) && aniId.ValueKind == JsonValueKind.Number && aniId.TryGetInt32(out var aniIdVal) ? aniIdVal : null,
                            CreatedAt = item.TryGetValue("created_at", out var created) && created.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(created.GetString(), out var createdAt) ? createdAt : DateTimeOffset.UtcNow,
                            UpdatedAt = item.TryGetValue("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(updated.GetString(), out var updatedAt) ? updatedAt : DateTimeOffset.UtcNow,
                        };
                        mangaList.Add(manga);
                    }
                    if (item.TryGetValue("total_count", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var totalVal))
                    {
                        totalCount = totalVal;
                    }
                }

                return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga list", ex.Message));
            }
        }

        /// <summary>
        /// Get popular manga
        /// </summary>
        /// <param name="days">The number of days to look back for views.</param>
        /// <param name="limit">The maximum number of results.</param>
        /// <param name="offset">The offset for pagination.</param>
        /// <returns>A list of popular manga.</returns>
        [HttpGet("list/popular")]
        [CacheControl(CacheDuration.OneHour, CacheDuration.TwelveHours)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetPopularManga([FromQuery, Range(1, 365)] int days = 30, [FromQuery, Range(1, 100)] int limit = 20, [FromQuery, Range(0, int.MaxValue)] int offset = 0)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.AdminClient.Rpc("get_manga_views_recent", new { p_days = days, p_limit = limit, p_offset = offset });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                    {
                        Items = new List<MangaResponse>(),
                        TotalItems = 0,
                        CurrentPage = (offset / limit) + 1,
                        PageSize = limit
                    }));
                }

                var popularManga = JsonSerializer.Deserialize<List<PopularMangaResponse>>(response.Content, _jsonOptions);

                var mangaList = (popularManga ?? new List<PopularMangaResponse>()).Select(p => new MangaResponse
                {
                    Id = p.Id,
                    Title = p.Title,
                    Cover = p.Cover,
                    Description = p.Description,
                    Status = p.Status,
                    Type = Enum.Parse<MangaType>(p.Type),
                    Authors = p.Authors,
                    Genres = p.Genres,
                    Views = (int)p.ViewCount,
                    AlternativeTitles = p.AlternativeTitles,
                    MalId = p.MalId,
                    AniId = p.AniId,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                }).ToList();

                var totalCount = popularManga?.FirstOrDefault()?.TotalCount ?? 0;

                return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = (int)totalCount,
                    CurrentPage = (offset / limit) + 1,
                    PageSize = limit
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve popular manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by ID
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>Detailed manga information.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaById(Guid id)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .From<MangaWithChaptersDto>()
                    .Where(m => m.Id == id)
                    .Select("*, chapters(*)")
                    .Single();

                if (response == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                var manga = response;
                var sortedChapters = manga.Chapters?.OrderBy(c => c.Number).ToList() ?? new List<ChapterDto>();
                var responseObj = new MangaDetailResponse
                {
                    Id = manga.Id,
                    Title = manga.Title,
                    Cover = manga.Cover,
                    Description = manga.Description,
                    Status = manga.Status,
                    Type = manga.Type,
                    Authors = manga.Authors,
                    Genres = manga.Genres,
                    Views = manga.Views,
                    Score = manga.Score,
                    AlternativeTitles = manga.AlternativeTitles,
                    MalId = manga.MalId,
                    AniId = manga.AniId,
                    CreatedAt = manga.CreatedAt,
                    UpdatedAt = manga.UpdatedAt,
                    Chapters = sortedChapters.Select(c => new MangaChapter
                    {
                        Id = c.Id,
                        Title = c.Title,
                        Number = c.Number,
                        Pages = c.Pages,
                        UpdatedAt = c.UpdatedAt,
                        CreatedAt = c.CreatedAt,
                    }).ToList()
                };

                return Ok(SuccessResponse<MangaDetailResponse>.Create(responseObj));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get chapters for a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>A list of chapters for the manga.</returns>
        [HttpGet("{id}/chapters")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaChapter>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaChapters(Guid id)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id)
                    .Select("*")
                    .Get();

                var sortedChapters = response.Models.OrderBy(c => c.Number).ToList();
                var chapters = sortedChapters.Select(c => new MangaChapter
                {
                    Id = c.Id,
                    Title = c.Title,
                    Number = c.Number,
                    Pages = c.Pages,
                    UpdatedAt = c.UpdatedAt,
                    CreatedAt = c.CreatedAt,
                }).ToList();

                return Ok(SuccessResponse<List<MangaChapter>>.Create(chapters));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve chapters", ex.Message));
            }
        }

        /// <summary>
        /// Update manga views
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        [HttpPost("{id}/view")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> ViewManga(Guid id)
        {
            var clientIp = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (string.IsNullOrEmpty(clientIp) || clientIp == "::1" || clientIp == "127.0.0.1")
            {
                clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
                if (string.IsNullOrEmpty(clientIp))
                {
                    clientIp = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
                    if (string.IsNullOrEmpty(clientIp))
                    {
                        clientIp = HttpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
                    }
                }
            }

            if (string.IsNullOrEmpty(clientIp))
            {
                return BadRequest(ErrorResponse.Create("Unable to determine client IP address"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var rpcResponse = await _supabaseService.AdminClient.Rpc("increment_manga_view", new { p_manga_id = id, p_ip = clientIp });
                if (rpcResponse.Content == "\"view_logged\"")
                {
                    return Ok(SuccessResponse<string>.Create("Views updated successfully"));
                }
                else if (rpcResponse.Content == "\"ignored_recent_view\"")
                {
                    return Ok(SuccessResponse<string>.Create("View recorded (recent view ignored)"));
                }
                else
                {
                    return StatusCode(500, ErrorResponse.Create("Unexpected response from RPC"));
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to update views", ex.Message));
            }
        }

        /// <summary>
        /// Rate a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="request">The rating request containing the rating value.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{id}/rate")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> RateManga(Guid id, [FromBody] RateMangaRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var rating = new MangaRatingDto
                {
                    UserId = userId,
                    MangaId = id,
                    Rating = request.Rating
                };

                var response = await _supabaseService.Client.From<MangaRatingDto>().Upsert(rating, new Supabase.Postgrest.QueryOptions { OnConflict = "user_id,manga_id" });
                return Ok(SuccessResponse<string>.Create("Rating submitted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to rate manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by MAL ID
        /// </summary>
        /// <param name="id">The MyAnimeList ID of the manga.</param>
        /// <returns>Detailed manga information.</returns>
        [HttpGet("mal/{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaByMalId(int id)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .From<MangaWithChaptersDto>()
                    .Where(m => m.MalId == id)
                    .Select("*, chapters(*)")
                    .Single();

                if (response == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                var manga = response;
                var sortedChapters = manga.Chapters?.OrderBy(c => c.Number).ToList() ?? new List<ChapterDto>();
                var responseObj = new MangaDetailResponse
                {
                    Id = manga.Id,
                    Title = manga.Title,
                    Cover = manga.Cover,
                    Description = manga.Description,
                    Status = manga.Status,
                    Type = manga.Type,
                    Authors = manga.Authors,
                    Genres = manga.Genres,
                    Views = manga.Views,
                    Score = manga.Score,
                    AlternativeTitles = manga.AlternativeTitles,
                    MalId = manga.MalId,
                    AniId = manga.AniId,
                    CreatedAt = manga.CreatedAt,
                    UpdatedAt = manga.UpdatedAt,
                    Chapters = sortedChapters.Select(c => new MangaChapter
                    {
                        Id = c.Id,
                        Title = c.Title,
                        Number = c.Number,
                        Pages = c.Pages,
                        UpdatedAt = c.UpdatedAt,
                        CreatedAt = c.CreatedAt,
                    }).ToList()
                };

                return Ok(SuccessResponse<MangaDetailResponse>.Create(responseObj));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by MAL IDs
        /// </summary>
        /// <param name="request">The request containing the list of MAL IDs.</param>
        /// <returns>A list of detailed manga information.</returns>
        [HttpPost("mal/batch")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> BatchGetMangaByMalIds([FromBody] BatchGetMangaRequest request)
        {
            if (request.MalIds == null || !request.MalIds.Any())
            {
                return BadRequest(ErrorResponse.Create("At least one MAL ID is required"));
            }

            if (request.MalIds.Count > 50)
            {
                return BadRequest(ErrorResponse.Create("Maximum 50 MAL IDs allowed per request"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .From<MangaWithChaptersDto>()
                    .Filter("mal_id", Supabase.Postgrest.Constants.Operator.In, request.MalIds)
                    .Select("*")
                    .Get();

                var mangaList = response.Models.Select(manga =>
                {
                    var sortedChapters = manga.Chapters?.OrderBy(c => c.Number).ToList() ?? new List<ChapterDto>();
                    return new MangaResponse
                    {
                        Id = manga.Id,
                        Title = manga.Title,
                        Cover = manga.Cover,
                        Description = manga.Description,
                        Status = manga.Status,
                        Type = manga.Type,
                        Authors = manga.Authors,
                        Genres = manga.Genres,
                        Views = manga.Views,
                        Score = manga.Score,
                        AlternativeTitles = manga.AlternativeTitles,
                        MalId = manga.MalId,
                        AniId = manga.AniId,
                        CreatedAt = manga.CreatedAt,
                        UpdatedAt = manga.UpdatedAt,
                    };
                }).ToList();

                return Ok(SuccessResponse<List<MangaResponse>>.Create(mangaList));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga chapter
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="subId">The chapter number.</param>
        /// <returns>The chapter details.</returns>
        [HttpGet("{id}/{subId}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<ChapterResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetChapter(Guid id, float subId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client.Rpc("get_chapter_by_manga_and_number", new { _manga_id = id.ToString(), _number = subId });

                if (string.IsNullOrEmpty(response.Content))
                    return NotFound(ErrorResponse.Create("Chapter not found", status: 404));

                var chapter = JsonSerializer.Deserialize<ChapterResponse>(response.Content, _jsonOptions);

                if (chapter == null)
                    return NotFound(ErrorResponse.Create("Chapter not found", status: 404));

                return Ok(SuccessResponse<ChapterResponse>.Create(chapter));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve chapter", ex.Message));
            }
        }

        /// <summary>
        /// Search manga
        /// </summary>
        /// <param name="query">The search query string.</param>
        /// <param name="limit">The maximum number of results.</param>
        /// <returns>A list of matching manga.</returns>
        [HttpGet("search")]
        [CacheControl(CacheDuration.OneHour, CacheDuration.SixHours)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaSearchResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SearchManga([FromQuery] string query, [FromQuery, Range(1, 100)] int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(ErrorResponse.Create("Search query is required"));

            if (limit > 100)
                limit = 100;
            if (limit < 1)
                limit = 1;

            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client.Rpc("search_manga", new { search_text = query, result_limit = limit });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<List<MangaSearchResponse>>.Create(new List<MangaSearchResponse>()));
                }

                var searchResults = JsonSerializer.Deserialize<List<MangaSearchResponse>>(response.Content, _jsonOptions);

                return Ok(SuccessResponse<List<MangaSearchResponse>>.Create(searchResults ?? new List<MangaSearchResponse>()));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to search manga", ex.Message));
            }
        }

    }
}