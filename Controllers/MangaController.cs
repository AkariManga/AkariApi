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
        /// Get latest manga
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of the latest manga.</returns>
        [HttpGet("list/latest")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes)]
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetLatestManga([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<MangaDto>()
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (clampedPage - 1) * clampedPageSize;

                var response = await _supabaseService.Client
                    .From<MangaDto>()
                    .Select("*")
                    .Order("updated_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var mangaList = response.Models.Select(m => new MangaResponse
                {
                    Id = m.Id,
                    Title = m.Title,
                    Cover = m.Cover,
                    Description = m.Description,
                    Status = m.Status,
                    Type = m.Type,
                    Authors = m.Authors,
                    Genres = m.Genres,
                    Views = m.Views,
                    Score = m.Score,
                    AlternativeTitles = m.AlternativeTitles,
                    MalId = m.MalId,
                    AniId = m.AniId,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt,
                }).ToList();

                return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve latest manga", ex.Message));
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
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetPopularManga([FromQuery, Range(1, 365)] int days = 30, [FromQuery, Range(1, 100)] int limit = 20, [FromQuery, Range(0, int.MaxValue)] int offset = 0)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.AdminClient.Rpc("get_manga_views_recent", new { p_days = days, p_limit = limit, p_offset = offset });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
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

                return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = (int)totalCount,
                    CurrentPage = (offset / limit) + 1,
                    PageSize = limit
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve popular manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by ID
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>Detailed manga information.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(ApiResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
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
                    return NotFound(ApiResponse<ErrorData>.Error("Manga not found", status: 404));

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

                return Ok(ApiResponse<MangaDetailResponse>.Success(responseObj));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Update manga views
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        [HttpPost("{id}/view")]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
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
                return BadRequest(ApiResponse<ErrorData>.Error("Unable to determine client IP address"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var rpcResponse = await _supabaseService.AdminClient.Rpc("increment_manga_view", new { p_manga_id = id, p_ip = clientIp });
                if (rpcResponse.Content == "\"count_incremented\"")
                {
                    return Ok(ApiResponse<string>.Success("Views updated successfully"));
                }
                else if (rpcResponse.Content == "\"ignored_recent_view\"")
                {
                    return Ok(ApiResponse<string>.Success("View recorded (recent view ignored)"));
                }
                else
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Unexpected response from RPC"));
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to update views", ex.Message));
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
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> RateManga(Guid id, [FromBody] RateMangaRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
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
                return Ok(ApiResponse<string>.Success("Rating submitted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to rate manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by MAL ID
        /// </summary>
        /// <param name="id">The MyAnimeList ID of the manga.</param>
        /// <returns>Detailed manga information.</returns>
        [HttpGet("mal/{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(ApiResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
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
                    return NotFound(ApiResponse<ErrorData>.Error("Manga not found", status: 404));

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

                return Ok(ApiResponse<MangaDetailResponse>.Success(responseObj));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by MAL IDs
        /// </summary>
        /// <param name="request">The request containing the list of MAL IDs.</param>
        /// <returns>A list of detailed manga information.</returns>
        [HttpPost("mal/batch")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(ApiResponse<List<MangaResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> BatchGetMangaByMalIds([FromBody] BatchGetMangaRequest request)
        {
            if (request.MalIds == null || !request.MalIds.Any())
            {
                return BadRequest(ApiResponse<ErrorData>.Error("At least one MAL ID is required"));
            }

            if (request.MalIds.Count > 50)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Maximum 50 MAL IDs allowed per request"));
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

                return Ok(ApiResponse<List<MangaResponse>>.Success(mangaList));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve manga", ex.Message));
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
        [ProducesResponseType(typeof(ApiResponse<ChapterResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetChapter(Guid id, float subId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client.Rpc("get_chapter_by_manga_and_number", new { _manga_id = id.ToString(), _number = subId });

                if (string.IsNullOrEmpty(response.Content))
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                var chapter = JsonSerializer.Deserialize<ChapterResponse>(response.Content, _jsonOptions);

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                return Ok(ApiResponse<ChapterResponse>.Success(chapter));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve chapter", ex.Message));
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
        [ProducesResponseType(typeof(ApiResponse<List<MangaSearchResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> SearchManga([FromQuery] string query, [FromQuery, Range(1, 100)] int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(ApiResponse<ErrorData>.Error("Search query is required"));

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
                    return Ok(ApiResponse<List<MangaSearchResponse>>.Success(new List<MangaSearchResponse>()));
                }

                var searchResults = JsonSerializer.Deserialize<List<MangaSearchResponse>>(response.Content, _jsonOptions);

                return Ok(ApiResponse<List<MangaSearchResponse>>.Success(searchResults ?? new List<MangaSearchResponse>()));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to search manga", ex.Message));
            }
        }
    }
}