using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Dapper;
using System.Collections.Concurrent;

namespace AkariApi.Controllers
{
    public enum MangaListSortOrder
    {
        latest,
        popular,
        newest,
        search,
    }

    [ApiController]
    [Route("v2/manga")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class MangaController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _ipSemaphores = new();

        public MangaController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        private static MangaResponse MapMangaRow(dynamic r)
        {
            return new MangaResponse
            {
                Id = (Guid)r.id,
                Title = r.title == null ? "" : (string)r.title,
                Cover = r.cover == null ? "" : (string)r.cover,
                Description = r.description == null ? "" : (string)r.description,
                Status = r.status == null ? "" : (string)r.status,
                Type = r.type == null ? MangaType.Manga : Enum.Parse<MangaType>((string)r.type, true),
                Authors = r.authors == null ? Array.Empty<string>() : (string[])r.authors,
                Genres = r.genres == null ? Array.Empty<string>() : (string[])r.genres,
                Views = r.view_count == null ? 0 : (r.view_count is long lv ? (lv > int.MaxValue ? int.MaxValue : (int)lv) : (int)r.view_count),
                Score = r.score == null ? 0m : (decimal)r.score,
                AlternativeTitles = r.alternative_titles == null ? null : (string[])r.alternative_titles,
                MalId = r.mal_id == null ? null : (int?)(long)r.mal_id > int.MaxValue ? int.MaxValue : (int?)(long)r.mal_id,
                AniId = r.ani_id == null ? null : (int?)(long)r.ani_id > int.MaxValue ? int.MaxValue : (int?)(long)r.ani_id,
                CreatedAt = r.created_at == null ? DateTimeOffset.UtcNow : (DateTimeOffset)(DateTime)r.created_at,
                UpdatedAt = r.updated_at == null ? DateTimeOffset.UtcNow : (DateTimeOffset)(DateTime)r.updated_at,
            };
        }

        private static short GetPagesCount(object? pagesVal)
        {
            if (pagesVal is short[] arr) return (short)arr.Length;
            if (pagesVal is short s) return s;
            if (pagesVal == null) return 0;
            return (short)0;
        }

        /// <summary>
        /// Get manga list
        /// </summary>
        /// <param name="sortBy">The sort order: 'popular', 'latest', 'newest'.</param>
        /// <param name="query">Search query string.</param>
        /// <param name="genres">Filter by genres.</param>
        /// <param name="authors">Filter by authors.</param>
        /// <param name="types">Filter by manga types.</param>
        /// <param name="excludedGenres">Exclude by genres.</param>
        /// <param name="excludedAuthors">Exclude by authors.</param>
        /// <param name="excludedTypes">Exclude by manga types.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of manga.</returns>
        [HttpGet("list")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaList(
            [FromQuery] MangaListSortOrder sortBy = MangaListSortOrder.latest,
            [FromQuery] string query = "",
            [FromQuery] string[]? genres = null,
            [FromQuery] string[]? authors = null,
            [FromQuery] string[]? types = null,
            [FromQuery] string[]? excludedGenres = null,
            [FromQuery] string[]? excludedAuthors = null,
            [FromQuery] string[]? excludedTypes = null,
            [FromQuery, Range(1, int.MaxValue)] int page = 1,
            [FromQuery, Range(1, 100)] int pageSize = 20
        )
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _postgresService.OpenAsync();

                var listQuery = @"WITH tsq AS (
    SELECT CASE
        WHEN @p_query::text IS NOT NULL AND @p_query::text != ''
        THEN plainto_tsquery('english', @p_query::text)
        ELSE NULL
    END AS query
),
total AS (
    SELECT COUNT(*) AS cnt
    FROM public.manga m, tsq
    WHERE
        (@p_genres::text[] IS NULL OR @p_genres::text[] <@ m.genres)
        AND (@p_authors::text[] IS NULL OR m.authors && @p_authors::text[])
        AND (@p_excluded_genres::text[] IS NULL OR NOT (m.genres && @p_excluded_genres::text[]))
        AND (@p_excluded_authors::text[] IS NULL OR NOT (m.authors && @p_excluded_authors::text[]))
        AND (tsq.query IS NULL OR m.search_vector @@ tsq.query)
        AND (@p_type::text[] IS NULL OR m.type = ANY(@p_type::text[]))
        AND (@p_excluded_type::text[] IS NULL OR m.type <> ALL(@p_excluded_type::text[]))
)
SELECT
    m.id,
    m.title,
    m.cover,
    m.description,
    m.status,
    m.type,
    m.authors,
    m.genres,
    m.mal_id,
    m.ani_id,
    m.created_at,
    m.updated_at,
    m.alternative_titles,
    m.score,
    m.view_count,
    total.cnt AS total_count
FROM public.manga m, tsq, total
WHERE
    (@p_genres::text[] IS NULL OR @p_genres::text[] <@ m.genres)
    AND (@p_authors::text[] IS NULL OR m.authors && @p_authors::text[])
    AND (@p_excluded_genres::text[] IS NULL OR NOT (m.genres && @p_excluded_genres::text[]))
    AND (@p_excluded_authors::text[] IS NULL OR NOT (m.authors && @p_excluded_authors::text[]))
    AND (tsq.query IS NULL OR m.search_vector @@ tsq.query)
    AND (@p_type::text[] IS NULL OR m.type = ANY(@p_type::text[]))
    AND (@p_excluded_type::text[] IS NULL OR m.type <> ALL(@p_excluded_type::text[]))
ORDER BY
    CASE
        WHEN @p_sort_by = 'popular' THEN m.view_count::float8
        WHEN @p_sort_by = 'latest' THEN EXTRACT(EPOCH FROM m.updated_at)
        WHEN @p_sort_by = 'newest' THEN EXTRACT(EPOCH FROM m.created_at)
        WHEN @p_sort_by = 'search' THEN
            CASE
                WHEN tsq.query IS NOT NULL THEN
                    ts_rank(m.search_vector, tsq.query)::float8 + (m.view_count::float8 / 100)
                ELSE m.view_count::float8
            END
    END DESC
LIMIT @p_limit OFFSET @p_offset";

                var dp = new DynamicParameters();
                dp.Add("p_authors", authors != null && authors.Length > 0 ? authors : null);
                dp.Add("p_genres", genres != null && genres.Length > 0 ? genres : null);
                dp.Add("p_type", types != null && types.Length > 0 ? types : null);
                dp.Add("p_excluded_authors", excludedAuthors != null && excludedAuthors.Length > 0 ? excludedAuthors : null);
                dp.Add("p_excluded_genres", excludedGenres != null && excludedGenres.Length > 0 ? excludedGenres : null);
                dp.Add("p_excluded_type", excludedTypes != null && excludedTypes.Length > 0 ? excludedTypes : null);
                dp.Add("p_limit", clampedPageSize);
                dp.Add("p_offset", offset);
                dp.Add("p_sort_by", sortBy.ToString());
                dp.Add("p_query", string.IsNullOrWhiteSpace(query) ? null : query);

                var rows = await _postgresService.Connection.QueryAsync(listQuery, dp);
                var mangaList = new List<MangaResponse>();
                long totalCount = 0;

                foreach (var r in rows)
                {
                    var manga = MapMangaRow(r);
                    mangaList.Add(manga);
                    if (totalCount == 0)
                    {
                        totalCount = (long)r.total_count;
                    }
                }

                await _postgresService.CloseAsync();

                if (totalCount == 0)
                {
                    return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                    {
                        Items = new List<MangaResponse>(),
                        TotalItems = 0,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
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
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga list", ex.Message));
            }
        }

        /// <summary>
        /// Get popular manga
        /// </summary>
        /// <param name="days">The number of days to look back for views.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of popular manga.</returns>
        [HttpGet("list/popular")]
        [CacheControl(CacheDuration.OneHour, CacheDuration.TwelveHours)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetPopularManga([FromQuery, Range(1, 365)] int days = 30, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _postgresService.OpenAsync();

                var popularQuery = @"
                    SELECT
                        m.id,
                        m.orig_id,
                        m.title,
                        m.cover,
                        m.description,
                        m.status,
                        m.type,
                        m.search_vector,
                        m.authors,
                        m.genres,
                        m.mal_id,
                        m.ani_id,
                        m.created_at,
                        m.updated_at,
                        m.alternative_titles,
                        m.score,
                        COUNT(v.id) AS view_count,
                        COUNT(*) OVER() AS total_count
                    FROM public.manga m
                    JOIN public.manga_views v ON v.manga_id = m.id
                    WHERE v.viewed_at > now() - (@p_days || ' days')::interval
                    GROUP BY
                        m.id, m.orig_id, m.title, m.cover, m.description,
                        m.status, m.type, m.search_vector, m.authors, m.genres,
                        m.mal_id, m.ani_id, m.created_at, m.updated_at,
                        m.alternative_titles, m.score
                    ORDER BY COUNT(v.id) DESC
                    LIMIT @p_limit OFFSET @p_offset";

                var rows = await _postgresService.Connection.QueryAsync(popularQuery, new { p_days = days, p_limit = clampedPageSize, p_offset = offset });
                var mangaList = new List<MangaResponse>();
                long totalCount = 0;

                foreach (var r in rows)
                {
                    var manga = MapMangaRow(r);
                    mangaList.Add(manga);
                    if (totalCount == 0)
                    {
                        totalCount = (long)r.total_count;
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
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve popular manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by ID
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>Basic manga information.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaById(Guid id)
        {
            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    SELECT m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count AS ""Views"", m.score, m.mal_id, m.ani_id, m.created_at, m.updated_at, m.alternative_titles
                    FROM manga m
                    WHERE m.id = @id";

                var manga = await _postgresService.Connection.QueryFirstOrDefaultAsync<MangaResponse>(query, new { id });

                await _postgresService.CloseAsync();

                if (manga == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                return Ok(SuccessResponse<MangaResponse>.Create(manga));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get detailed manga information by ID
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>Detailed manga information with chapters.</returns>
        [HttpGet("{id}/details")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaDetailsById(Guid id)
        {
            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    SELECT m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count, m.score, m.mal_id, m.ani_id, m.created_at, m.updated_at, m.alternative_titles,
                           json_agg(row_to_json(c)) as chapters_json
                    FROM manga m
                    LEFT JOIN chapters c ON m.id = c.manga_id
                    WHERE m.id = @id
                    GROUP BY m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count, m.score, m.mal_id, m.ani_id, m.created_at, m.updated_at, m.alternative_titles";

                MangaDetailResponse? manga = null;
                List<MangaChapter> chapters = new();

                var row = await _postgresService.Connection.QueryFirstOrDefaultAsync(query, new { id });
                if (row != null)
                {
                    manga = new MangaDetailResponse
                    {
                        Id = (Guid)row.id,
                        Title = (string)row.title,
                        Cover = (string)row.cover,
                        Description = (string)row.description,
                        Status = (string)row.status,
                        Type = Enum.Parse<MangaType>((string)row.type),
                        Authors = (string[])row.authors,
                        Genres = (string[])row.genres,
                        Views = (long)row.view_count > int.MaxValue ? int.MaxValue : (int)(long)row.view_count,
                        Score = (decimal)row.score,
                        MalId = row.mal_id == null ? null : ((long)row.mal_id > int.MaxValue ? int.MaxValue : (int?)(long)row.mal_id),
                        AniId = row.ani_id == null ? null : ((long)row.ani_id > int.MaxValue ? int.MaxValue : (int?)(long)row.ani_id),
                        CreatedAt = (DateTime)row.created_at,
                        UpdatedAt = (DateTime)row.updated_at,
                        AlternativeTitles = row.alternative_titles == null ? null : (string[])row.alternative_titles
                    };

                    var chaptersJson = row.chapters_json == null ? "[]" : (string)row.chapters_json;
                    try
                    {
                        chapters = JsonSerializer.Deserialize<List<MangaChapter>>(chaptersJson, _jsonOptions) ?? new List<MangaChapter>();
                    }
                    catch (JsonException)
                    {
                        chapters = new List<MangaChapter>();
                    }
                }

                await _postgresService.CloseAsync();

                if (manga == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                manga.Chapters = chapters.OrderBy(c => c.Number).ToList();

                return Ok(SuccessResponse<MangaDetailResponse>.Create(manga));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
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
                await _postgresService.OpenAsync();

                var rows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @id ORDER BY number DESC",
                    new { id });

                var chapters = rows.Select(r =>
                {
                    var pagesVal = (object?)r.pages;
                    return new MangaChapter
                    {
                        Id = (Guid)r.id,
                        Title = (string)r.title,
                        Number = (float)r.number,
                        Pages = GetPagesCount(pagesVal),
                        UpdatedAt = (DateTime)r.updated_at,
                        CreatedAt = (DateTime)r.created_at,
                    };
                }).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaChapter>>.Create(chapters));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve chapters", ex.Message));
            }
        }

        /// <summary>
        /// Get recommended manga for a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="limit">The maximum number of recommendations to return.</param>
        /// <returns>A list of recommended manga.</returns>
        [HttpGet("{id}/recommendations")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaRecommendations(Guid id, [FromQuery, Range(1, 100)] int limit = 20)
        {
            try
            {
                await _postgresService.OpenAsync();

                var exists = await _postgresService.Connection.ExecuteScalarAsync<int?>(
                    "SELECT 1 FROM manga WHERE id = @id LIMIT 1",
                    new { id });

                if (exists == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Manga not found"));
                }

                var query = @"
                    SELECT m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count AS ""Views"", m.score, m.alternative_titles, m.mal_id, m.ani_id, m.created_at, m.updated_at
                    FROM manga_similarities ms
                    JOIN manga m ON ms.similar_manga_id = m.id
                    WHERE ms.manga_id = @mangaId
                    ORDER BY ms.hybrid_similarity_score DESC
                    LIMIT @limit
                ";

                var recommendations = (await _postgresService.Connection.QueryAsync<MangaResponse>(query, new { mangaId = id, limit })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaResponse>>.Create(recommendations));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("An error occurred while fetching recommendations", ex.Message));
            }
        }

        private static bool IsLocalOrPrivateIp(System.Net.IPAddress ip)
        {
            if (ip.Equals(System.Net.IPAddress.Loopback))
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                return bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                return (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                    (bytes[0] == 0xfc || bytes[0] == 0xfd);
            }

            return false;
        }

        /// <summary>
        /// Update manga views
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="request">Optional request body for view tracking preferences.</param>
        [HttpPost("{id}/view")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> ViewManga(Guid id, [FromBody] ViewMangaRequest? request = null)
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

            if (!System.Net.IPAddress.TryParse(clientIp, out var ipAddress))
            {
                return BadRequest(ErrorResponse.Create("Invalid client IP address"));
            }

            if (IsLocalOrPrivateIp(ipAddress))
            {
                return Ok(SuccessResponse<string>.Create("View ignored (local or private IP)"));
            }

            Guid? userId = null;
            if (request?.SaveUserId == true)
            {
                var (authUserId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (string.IsNullOrEmpty(errorMessage))
                {
                    userId = authUserId;
                }
            }

            var semaphore = _ipSemaphores.GetOrAdd(clientIp, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();

            try
            {
                try
                {
                    await _postgresService.OpenAsync();

                    var query = @"
WITH recent AS (
    SELECT EXISTS (
        SELECT 1
        FROM public.manga_views
        WHERE manga_id = @p_manga_id
          AND ip = @p_ip::inet
          AND viewed_at > now() - interval '24 hours'
    ) AS is_recent
), ins AS (
    INSERT INTO public.manga_views (manga_id, ip, user_id)
    SELECT @p_manga_id, @p_ip::inet, @p_user_id
    WHERE NOT (SELECT is_recent FROM recent)
    RETURNING 1
)
SELECT CASE
    WHEN (SELECT is_recent FROM recent) THEN 'ignored_recent_view'
    ELSE 'view_logged'
END;";

                    var content = await _postgresService.Connection.ExecuteScalarAsync<string?>(query, new
                    {
                        p_manga_id = id,
                        p_ip = ipAddress.ToString(),
                        p_user_id = userId == null ? (object)DBNull.Value : userId
                    });

                    await _postgresService.CloseAsync();

                    if (content == "view_logged")
                    {
                        return Ok(SuccessResponse<string>.Create("Views updated successfully"));
                    }
                    else if (content == "ignored_recent_view")
                    {
                        return Ok(SuccessResponse<string>.Create("View recorded (recent view ignored)"));
                    }
                    else
                    {
                        return StatusCode(500, ErrorResponse.Create("Unexpected response from RPC", content));
                    }
                }
                catch (Exception ex)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to update views", ex.Message));
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Get user's recently viewed manga
        /// </summary>
        /// <param name="limit">The maximum number of unique manga to return.</param>
        /// <returns>A list of recently viewed manga.</returns>
        [HttpGet("viewed")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetRecentlyViewedManga([FromQuery, Range(1, 100)] int limit = 20)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _postgresService.OpenAsync();

                var query = @"
SELECT
    m.id,
    m.title,
    m.cover,
    m.description,
    m.status,
    m.type,
    m.authors,
    m.genres,
    m.view_count AS ""Views"",
    m.score,
    m.alternative_titles,
    m.mal_id,
    m.ani_id,
    m.created_at,
    m.updated_at
FROM (
    SELECT DISTINCT ON (manga_id) manga_id, viewed_at
    FROM public.manga_views
    WHERE user_id = @p_user_id
    ORDER BY manga_id, viewed_at DESC
) mv
JOIN public.manga m ON m.id = mv.manga_id
ORDER BY mv.viewed_at DESC
LIMIT @p_limit;";

                var mangaList = (await _postgresService.Connection.QueryAsync<MangaResponse>(query, new { p_user_id = userId, p_limit = limit })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaResponse>>.Create(mangaList));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve recently viewed manga", ex.Message));
            }
        }

        /// <summary>
        /// Rate a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <param name="request">The rating request containing the rating value.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{id}/rate")]
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
                await _postgresService.OpenAsync();

                await _postgresService.Connection.ExecuteAsync(
                    "INSERT INTO manga_ratings (user_id, manga_id, rating) VALUES (@userId, @mangaId, @rating) ON CONFLICT (user_id, manga_id) DO UPDATE SET rating = EXCLUDED.rating",
                    new { userId, mangaId = id, rating = request.Rating });

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<string>.Create("Rating submitted successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to rate manga", ex.Message));
            }
        }

        /// <summary>
        /// Get user's rating for a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>The user's rating for the manga.</returns>
        [HttpGet("{id}/rating")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<int?>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaRating(Guid id)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _postgresService.OpenAsync();

                var result = await _postgresService.Connection.ExecuteScalarAsync<object?>(
                    "SELECT rating FROM manga_ratings WHERE user_id = @userId AND manga_id = @mangaId",
                    new { userId, mangaId = id });

                await _postgresService.CloseAsync();

                int? rating = result != null ? (int?)Convert.ToInt32(result) : null;
                return Ok(SuccessResponse<int?>.Create(rating));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to get rating", ex.Message));
            }
        }

        /// <summary>
        /// Remove user's rating for a manga
        /// </summary>
        /// <param name="id">The unique identifier of the manga.</param>
        /// <returns>A success message.</returns>
        [HttpDelete("{id}/rate")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> DeleteMangaRating(Guid id)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _postgresService.OpenAsync();

                await _postgresService.Connection.ExecuteAsync(
                    "DELETE FROM manga_ratings WHERE user_id = @userId AND manga_id = @mangaId",
                    new { userId, mangaId = id });

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<string>.Create("Rating removed successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to remove rating", ex.Message));
            }
        }

        /// <summary>
        /// Batch rate multiple manga
        /// </summary>
        /// <param name="request">The request containing manga IDs and their ratings.</param>
        /// <returns>A success message.</returns>
        [HttpPost("rate/batch")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> BatchRateManga([FromBody] BatchRateMangaRequest request)
        {
            if (request.Ratings == null || !request.Ratings.Any())
                return BadRequest(ErrorResponse.Create("At least one rating entry is required"));

            if (request.Ratings.Count > 50)
                return BadRequest(ErrorResponse.Create("Cannot rate more than 50 manga at once"));

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));

            try
            {
                await _postgresService.OpenAsync();

                var mangaIds = request.Ratings.Select(r => r.MangaId).ToArray();
                var ratings = request.Ratings.Select(r => r.Rating).ToArray();

                await _postgresService.Connection.ExecuteAsync(@"
                    INSERT INTO manga_ratings (user_id, manga_id, rating)
                    SELECT @userId, unnest(@mangaIds::uuid[]), unnest(@ratings::int[])
                    ON CONFLICT (user_id, manga_id) DO UPDATE SET rating = EXCLUDED.rating",
                    new { userId, mangaIds, ratings });

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<string>.Create($"{request.Ratings.Count} rating(s) submitted successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to submit ratings", ex.Message));
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
                await _postgresService.OpenAsync();

                var mangaQuery = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE mal_id = @id";
                var mangaRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(mangaQuery, new { id });

                if (mangaRow == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));
                }

                var manga = new MangaDetailResponse
                {
                    Id = (Guid)mangaRow.id,
                    Title = (string)mangaRow.title,
                    Cover = (string)mangaRow.cover,
                    Description = (string)mangaRow.description,
                    Status = (string)mangaRow.status,
                    Type = Enum.Parse<MangaType>((string)mangaRow.type),
                    Authors = (string[])mangaRow.authors,
                    Genres = (string[])mangaRow.genres,
                    Views = (long)mangaRow.view_count > int.MaxValue ? int.MaxValue : (int)(long)mangaRow.view_count,
                    Score = (decimal)mangaRow.score,
                    MalId = mangaRow.mal_id == null ? null : ((long)mangaRow.mal_id > int.MaxValue ? int.MaxValue : (int?)(long)mangaRow.mal_id),
                    AniId = mangaRow.ani_id == null ? null : ((long)mangaRow.ani_id > int.MaxValue ? int.MaxValue : (int?)(long)mangaRow.ani_id),
                    CreatedAt = (DateTime)mangaRow.created_at,
                    UpdatedAt = (DateTime)mangaRow.updated_at,
                    AlternativeTitles = mangaRow.alternative_titles == null ? null : (string[])mangaRow.alternative_titles
                };

                var chapterRows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @mangaId ORDER BY number",
                    new { mangaId = manga.Id });

                var chapters = chapterRows.Select(r =>
                {
                    var pagesVal = (object?)r.pages;
                    return new MangaChapter
                    {
                        Id = (Guid)r.id,
                        Title = (string)r.title,
                        Number = (float)r.number,
                        Pages = GetPagesCount(pagesVal),
                        UpdatedAt = (DateTime)r.updated_at,
                        CreatedAt = (DateTime)r.created_at
                    };
                }).ToList();

                await _postgresService.CloseAsync();

                manga.Chapters = chapters.OrderBy(c => c.Number).ToList();

                return Ok(SuccessResponse<MangaDetailResponse>.Create(manga));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
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
                await _postgresService.OpenAsync();

                var query = "SELECT id, title, cover, description, status, type, authors, genres, view_count AS \"Views\", score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE mal_id = ANY(@malIds)";
                var mangaList = (await _postgresService.Connection.QueryAsync<MangaResponse>(query, new { malIds = request.MalIds.ToArray() })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaResponse>>.Create(mangaList));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by AniList ID
        /// </summary>
        /// <param name="id">The AniList ID of the manga.</param>
        /// <returns>Detailed manga information.</returns>
        [HttpGet("ani/{id}")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaByAniId(int id)
        {
            try
            {
                await _postgresService.OpenAsync();

                var mangaQuery = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE ani_id = @id";
                var mangaRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(mangaQuery, new { id });

                if (mangaRow == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));
                }

                var manga = new MangaDetailResponse
                {
                    Id = (Guid)mangaRow.id,
                    Title = (string)mangaRow.title,
                    Cover = (string)mangaRow.cover,
                    Description = (string)mangaRow.description,
                    Status = (string)mangaRow.status,
                    Type = Enum.Parse<MangaType>((string)mangaRow.type),
                    Authors = (string[])mangaRow.authors,
                    Genres = (string[])mangaRow.genres,
                    Views = (long)mangaRow.view_count > int.MaxValue ? int.MaxValue : (int)(long)mangaRow.view_count,
                    Score = (decimal)mangaRow.score,
                    MalId = mangaRow.mal_id == null ? null : ((long)mangaRow.mal_id > int.MaxValue ? int.MaxValue : (int?)(long)mangaRow.mal_id),
                    AniId = mangaRow.ani_id == null ? null : ((long)mangaRow.ani_id > int.MaxValue ? int.MaxValue : (int?)(long)mangaRow.ani_id),
                    CreatedAt = (DateTime)mangaRow.created_at,
                    UpdatedAt = (DateTime)mangaRow.updated_at,
                    AlternativeTitles = mangaRow.alternative_titles == null ? null : (string[])mangaRow.alternative_titles
                };

                var chapterRows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @mangaId ORDER BY number",
                    new { mangaId = manga.Id });

                var chapters = chapterRows.Select(r =>
                {
                    var pagesVal = (object?)r.pages;
                    return new MangaChapter
                    {
                        Id = (Guid)r.id,
                        Title = (string)r.title,
                        Number = (float)r.number,
                        Pages = GetPagesCount(pagesVal),
                        UpdatedAt = (DateTime)r.updated_at,
                        CreatedAt = (DateTime)r.created_at
                    };
                }).ToList();

                await _postgresService.CloseAsync();

                manga.Chapters = chapters.OrderBy(c => c.Number).ToList();

                return Ok(SuccessResponse<MangaDetailResponse>.Create(manga));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga", ex.Message));
            }
        }

        /// <summary>
        /// Get manga by AniList IDs
        /// </summary>
        /// <param name="request">The request containing the list of AniList IDs.</param>
        /// <returns>A list of detailed manga information.</returns>
        [HttpPost("ani/batch")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<List<MangaResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> BatchGetMangaByAniIds([FromBody] BatchGetAniMangaRequest request)
        {
            if (request.AniIds == null || !request.AniIds.Any())
            {
                return BadRequest(ErrorResponse.Create("At least one AniList ID is required"));
            }

            if (request.AniIds.Count > 50)
            {
                return BadRequest(ErrorResponse.Create("Maximum 50 AniList IDs allowed per request"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var query = "SELECT id, title, cover, description, status, type, authors, genres, view_count AS \"Views\", score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE ani_id = ANY(@aniIds)";
                var mangaList = (await _postgresService.Connection.QueryAsync<MangaResponse>(query, new { aniIds = request.AniIds.ToArray() })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaResponse>>.Create(mangaList));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
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
                await _postgresService.OpenAsync();

                var rpcQuery = @"
                SELECT to_jsonb(t) FROM (
                SELECT
                    c.id,
                    c.manga_id,
                    c.title,
                    c.number,
                    (
                        SELECT json_agg(
                            json_build_object('value', ch.number::text, 'label', ch.title)
                            ORDER BY ch.number DESC
                        )
                        FROM public.chapters ch
                        WHERE ch.manga_id = c.manga_id
                    ) AS chapters,
                    c.pages,
                    (
                        SELECT ch_next.number
                        FROM public.chapters ch_next
                        WHERE ch_next.manga_id = c.manga_id AND ch_next.number > c.number
                        ORDER BY ch_next.number ASC
                        LIMIT 1
                    ) AS next_chapter,
                    (
                        SELECT ch_prev.number
                        FROM public.chapters ch_prev
                        WHERE ch_prev.manga_id = c.manga_id AND ch_prev.number < c.number
                        ORDER BY ch_prev.number DESC
                        LIMIT 1
                    ) AS last_chapter,
                    c.images,
                    m.title AS manga_title,
                    m.type::text,
                    m.mal_id,
                    m.ani_id
                FROM public.chapters c
                JOIN public.manga m ON m.id = c.manga_id
                WHERE c.manga_id = @_manga_id
                    AND c.number = @_number
                ) t";

                var content = await _postgresService.Connection.ExecuteScalarAsync<string?>(rpcQuery, new { _manga_id = id, _number = subId });

                await _postgresService.CloseAsync();

                if (string.IsNullOrEmpty(content))
                    return NotFound(ErrorResponse.Create("Chapter not found", status: 404));

                ChapterResponse? chapter = null;
                try
                {
                    chapter = JsonSerializer.Deserialize<ChapterResponse>(content, _jsonOptions);
                }
                catch (JsonException)
                {
                    return StatusCode(500, ErrorResponse.Create("Failed to parse chapter data", "Invalid JSON response from database"));
                }

                if (chapter == null)
                    return NotFound(ErrorResponse.Create("Chapter not found", status: 404));

                return Ok(SuccessResponse<ChapterResponse>.Create(chapter));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
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
                await _postgresService.OpenAsync();

                var searchQuery = @"
                    SELECT id, title, cover, description, status, type, authors, genres, view_count AS ""Views"", score, mal_id, ani_id, created_at, updated_at, alternative_titles
                    FROM manga
                    WHERE search_vector @@ plainto_tsquery('english', @query)
                    ORDER BY (ts_rank(search_vector, plainto_tsquery('english', @query)) + (view_count::float / 100)) DESC
                    LIMIT @limit";

                var mangaList = (await _postgresService.Connection.QueryAsync<MangaSearchResponse>(searchQuery, new { query, limit })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<MangaSearchResponse>>.Create(mangaList));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to search manga", ex.Message));
            }
        }

        /// <summary>
        /// Get list of manga IDs
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of manga IDs.</returns>
        [HttpGet("ids")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaIdsResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaIds(
            [FromQuery, Range(1, int.MaxValue)] int page = 1,
            [FromQuery, Range(1, 1000)] int pageSize = 100
        )
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize, 1000, 100);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _postgresService.OpenAsync();

                var rows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, COUNT(*) OVER() as total FROM manga ORDER BY view_count DESC LIMIT @limit OFFSET @offset",
                    new { limit = clampedPageSize, offset });

                var ids = new List<Guid>();
                long totalCount = 0;
                foreach (var r in rows)
                {
                    ids.Add((Guid)r.id);
                    if (totalCount == 0)
                    {
                        totalCount = (long)r.total;
                    }
                }

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<MangaIdsResponse>.Create(new MangaIdsResponse
                {
                    Items = ids,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga IDs", ex.Message));
            }
        }

        /// <summary>
        /// Get list of manga with their chapter IDs
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of manga with their chapter IDs.</returns>
        [HttpGet("chapter/ids")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.OneHour)]
        [ProducesResponseType(typeof(SuccessResponse<MangaChapterIdsResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMangaChapterIds(
            [FromQuery, Range(1, int.MaxValue)] int page = 1,
            [FromQuery, Range(1, 500)] int pageSize = 100
        )
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize, 1000, 100);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    WITH page_manga AS (
                        SELECT id, COUNT(*) OVER() as total
                        FROM manga
                        ORDER BY view_count DESC
                        OFFSET @offset LIMIT @limit
                    )
                    SELECT pm.id,
                        (SELECT array_agg(number ORDER BY number)
                            FROM chapters
                            WHERE manga_id = pm.id) as chapter_numbers,
                        pm.total
                    FROM page_manga pm
                    ORDER BY pm.id";

                var rows = await _postgresService.Connection.QueryAsync(query, new { limit = clampedPageSize, offset });
                var pairs = new List<MangaChapterIdsPair>();
                long totalCount = 0;

                foreach (var r in rows)
                {
                    var chapterNumbers = r.chapter_numbers == null ? Array.Empty<float>() : (float[])r.chapter_numbers;
                    if (totalCount == 0)
                    {
                        totalCount = (long)r.total;
                    }
                    pairs.Add(new MangaChapterIdsPair
                    {
                        MangaId = (Guid)r.id,
                        ChapterIds = chapterNumbers.ToList()
                    });
                }

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<MangaChapterIdsResponse>.Create(new MangaChapterIdsResponse
                {
                    Items = pairs,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga chapter IDs", ex.Message));
            }
        }
    }
}
