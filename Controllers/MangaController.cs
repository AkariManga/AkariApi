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
        /// <param name="genres">Filter by genres.</param>
        /// <param name="authors">Filter by authors.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of manga.</returns>
        [HttpGet("list")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetMangaList(
            [FromQuery] string sortBy = "latest",
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
                    p_authors = authors
                });

                if (string.IsNullOrEmpty(rpcResponse.Content))
                {
                    return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
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

                return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve manga list", ex.Message));
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
        /// Get chapters for a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>A list of chapters for the manga.</returns>
        [HttpGet("{id}/chapters")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(ApiResponse<List<MangaChapter>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
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

                return Ok(ApiResponse<List<MangaChapter>>.Success(chapters));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve chapters", ex.Message));
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

        /// <summary>
        /// Get comments for a chapter
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="subId">The chapter number.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of top-level comments for the chapter.</returns>
        [HttpGet("{id}/{subId}/comments")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(ApiResponse<PaginatedCommentResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetChapterComments(Guid id, float subId, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                // First get the chapter
                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id)
                    .Where(c => c.Number == subId)
                    .Single();

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                var totalCountResponse = await _supabaseService.Client.Rpc("get_top_level_comment_count", new { p_chapter_id = chapter.Id });
                var totalCount = string.IsNullOrEmpty(totalCountResponse.Content) ? 0 : JsonSerializer.Deserialize<int>(totalCountResponse.Content);

                var response = await _supabaseService.Client.Rpc("get_comments_with_reply_count", new { p_chapter_id = chapter.Id, p_limit = clampedPageSize, p_offset = (clampedPage - 1) * clampedPageSize });

                if (response == null || string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<PaginatedCommentResponse>.Success(new PaginatedCommentResponse
                    {
                        Items = new List<CommentResponse>(),
                        TotalItems = 0,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var paginatedCommentsDto = JsonSerializer.Deserialize<List<TopCommentWithRepliesResponse>>(response.Content, _jsonOptions) ?? new List<TopCommentWithRepliesResponse>();

                var paginatedComments = paginatedCommentsDto.Select(c => new CommentResponse
                {
                    Id = c.Id,
                    ChapterId = c.ChapterId,
                    UserProfile = c.UserProfile,
                    ParentId = c.ParentId,
                    Content = c.Content,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Edited = c.Edited,
                    Deleted = c.Deleted,
                    Upvotes = c.Upvotes,
                    Downvotes = c.Downvotes,
                    ReplyCount = c.ReplyCount,
                    Attachment = c.Attachment
                }).ToList();

                return Ok(ApiResponse<PaginatedCommentResponse>.Success(new PaginatedCommentResponse
                {
                    Items = paginatedComments,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve comments", ex.Message));
            }
        }

        /// <summary>
        /// Get replies for a specific comment
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="subId">The chapter number.</param>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A list of replies for the comment.</returns>
        [HttpGet("{id}/{subId}/comments/{commentId}/replies")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(ApiResponse<List<CommentWithRepliesResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetCommentReplies(Guid id, float subId, Guid commentId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                // First get the chapter
                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id)
                    .Where(c => c.Number == subId)
                    .Single();

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                var response = await _supabaseService.Client.Rpc("get_comment_replies_recursive", new { p_comment_id = commentId });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<List<CommentWithRepliesResponse>>.Success(new List<CommentWithRepliesResponse>()));
                }

                var allReplies = JsonSerializer.Deserialize<List<CommentWithRepliesResponse>>(response.Content, _jsonOptions) ?? new List<CommentWithRepliesResponse>();

                var replies = BuildReplyTree(allReplies, commentId);
                return Ok(ApiResponse<List<CommentWithRepliesResponse>>.Success(replies));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve comment replies", ex.Message));
            }
        }

        /// <summary>
        /// Get user's votes on comments in a chapter
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="subId">The chapter number.</param>
        /// <returns>A list of the user's votes on comments in the chapter.</returns>
        [HttpGet("{id}/{subId}/comments/votes")]
        [ProducesResponseType(typeof(ApiResponse<List<CommentVoteResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        [RequireTokenRefresh]
        public async Task<IActionResult> GetUserCommentVotesInChapter(Guid id, float subId)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();
                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id && c.Number == subId)
                    .Single();

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                var response = await _supabaseService.Client.Rpc("get_user_comment_votes_in_chapter", new { p_user_id = userId, p_chapter_id = chapter.Id });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<List<CommentVoteResponse>>.Success(new List<CommentVoteResponse>()));
                }

                var votes = JsonSerializer.Deserialize<List<CommentVoteResponse>>(response.Content, _jsonOptions) ?? new List<CommentVoteResponse>();

                return Ok(ApiResponse<List<CommentVoteResponse>>.Success(votes));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve user comment votes", ex.Message));
            }
        }

        private static List<CommentWithRepliesResponse> BuildReplyTree(List<CommentWithRepliesResponse> allReplies, Guid parentId)
        {
            var directReplies = allReplies.Where(r => r.ParentId == parentId).ToList();

            foreach (var reply in directReplies)
            {
                reply.Replies = BuildReplyTree(allReplies, reply.Id);
            }

            return directReplies.OrderBy(r => r.CreatedAt).ToList();
        }

        /// <summary>
        /// Create a comment on a chapter
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="subId">The chapter number.</param>
        /// <param name="request">The comment request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{id}/{subId}/comments")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<CommentResponse>), 201)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> CreateComment(Guid id, float subId, [FromBody] CreateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                // Get the chapter
                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id && c.Number == subId)
                    .Single();

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                // If parent comment exists, validate it belongs to the same chapter
                if (request.ParentId.HasValue)
                {
                    var parentComment = await _supabaseService.Client
                        .From<ChapterCommentDto>()
                        .Where(c => c.Id == request.ParentId.Value)
                        .Single();

                    if (parentComment == null || parentComment.ChapterId != chapter.Id)
                        return BadRequest(ApiResponse<ErrorData>.Error("Invalid parent comment"));
                }

                var comment = new ChapterCommentDto
                {
                    ChapterId = chapter.Id,
                    UserId = userId,
                    ParentId = request.ParentId,
                    Content = request.Content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Edited = false,
                    Deleted = false,
                    Upvotes = 0,
                    Downvotes = 0,
                    AttachmentId = request.AttachmentId
                };

                var response = await _supabaseService.Client.From<ChapterCommentDto>().Insert(comment);

                var createdComment = response.Models.FirstOrDefault();
                if (createdComment == null)
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to create comment"));

                var userProfile = await _supabaseService.Client.From<ProfileDto>().Where(p => p.Id == userId).Single();

                if (userProfile == null)
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve user profile"));

                var commentResponse = new CommentResponse
                {
                    Id = createdComment.Id,
                    ChapterId = createdComment.ChapterId,
                    UserProfile = new UserProfile
                    {
                        Id = userProfile.Id,
                        Username = userProfile.Username,
                        DisplayName = userProfile.DisplayName
                    },
                    ParentId = createdComment.ParentId,
                    Content = createdComment.Content,
                    CreatedAt = createdComment.CreatedAt,
                    UpdatedAt = createdComment.UpdatedAt,
                    Edited = createdComment.Edited,
                    Deleted = createdComment.Deleted,
                    Upvotes = createdComment.Upvotes,
                    Downvotes = createdComment.Downvotes
                };

                return Created("", ApiResponse<CommentResponse>.Success(commentResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to create comment", ex.Message));
            }
        }

        /// <summary>
        /// Update a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <param name="request">The update request.</param>
        /// <returns>A success message.</returns>
        [HttpPut("comment/{commentId}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 403)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> UpdateComment(Guid commentId, [FromBody] UpdateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<ChapterCommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ApiResponse<ErrorData>.Error("Forbidden", "You can only edit your own comments"));

                if (comment.Deleted)
                    return BadRequest(ApiResponse<ErrorData>.Error("Cannot edit deleted comment"));

                comment.Content = request.Content;
                comment.UpdatedAt = DateTimeOffset.UtcNow;
                comment.Edited = true;

                await _supabaseService.Client.From<ChapterCommentDto>().Update(comment);

                return Ok(ApiResponse<string>.Success("Comment updated successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to update comment", ex.Message));
            }
        }

        /// <summary>
        /// Delete a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A success message.</returns>
        [HttpDelete("comment/{commentId}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 403)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> DeleteComment(Guid commentId)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<ChapterCommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ApiResponse<ErrorData>.Error("Forbidden", "You can only delete your own comments"));

                // Soft delete
                comment.Deleted = true;
                comment.Content = "[deleted]";
                comment.UpdatedAt = DateTimeOffset.UtcNow;

                await _supabaseService.Client.From<ChapterCommentDto>().Update(comment);

                return Ok(ApiResponse<string>.Success("Comment deleted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to delete comment", ex.Message));
            }
        }

        /// <summary>
        /// Vote on a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <param name="request">The vote request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("comment/{commentId}/vote")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> VoteComment(Guid commentId, [FromBody] VoteCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<ChapterCommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.Deleted)
                    return BadRequest(ApiResponse<ErrorData>.Error("Cannot vote on deleted comment"));

                // Check if user already voted
                var existingVote = await _supabaseService.Client
                    .From<ChapterCommentVoteDto>()
                    .Where(v => v.CommentId == commentId && v.UserId == userId)
                    .Single();

                if (existingVote != null)
                {
                    existingVote.Value = request.Value;
                    await _supabaseService.Client.From<ChapterCommentVoteDto>().Update(existingVote);
                }
                else
                {
                    var vote = new ChapterCommentVoteDto
                    {
                        CommentId = commentId,
                        UserId = userId,
                        Value = request.Value
                    };

                    await _supabaseService.Client.From<ChapterCommentVoteDto>().Insert(vote);
                }

                return Ok(ApiResponse<string>.Success("Vote recorded successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to vote on comment", ex.Message));
            }
        }
    }
}