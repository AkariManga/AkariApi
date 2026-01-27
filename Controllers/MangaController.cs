using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Npgsql;
using NpgsqlTypes;
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

                var listQuery = @"SELECT
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
    m.view_count,
    COUNT(*) OVER() AS total_count
FROM public.manga m
WHERE
    (@p_genres IS NULL OR @p_genres <@ m.genres)
    AND (@p_authors IS NULL OR m.authors && @p_authors)
    AND (@p_excluded_genres IS NULL OR NOT (m.genres && @p_excluded_genres))
    AND (@p_excluded_authors IS NULL OR NOT (m.authors && @p_excluded_authors))
    AND (@p_query IS NULL OR @p_query = '' OR m.search_vector @@ plainto_tsquery('english', @p_query))
    AND (@p_type IS NULL OR m.type = ANY(@p_type))
    AND (@p_excluded_type IS NULL OR m.type <> ALL(@p_excluded_type))
ORDER BY
    CASE
        WHEN @p_sort_by = 'popular' THEN m.view_count
        WHEN @p_sort_by = 'latest' THEN EXTRACT(EPOCH FROM m.updated_at)
        WHEN @p_sort_by = 'newest' THEN EXTRACT(EPOCH FROM m.created_at)
        WHEN @p_sort_by = 'search' THEN
            CASE
                WHEN @p_query IS NOT NULL AND @p_query != '' THEN
                    ts_rank(m.search_vector, plainto_tsquery('english', @p_query)) + (m.view_count::float / 100)
                ELSE m.view_count
            END
    END DESC
LIMIT @p_limit OFFSET @p_offset";

                var mangaList = new List<MangaResponse>();
                long totalCount = 0;
                using (var cmd = new NpgsqlCommand(listQuery, _postgresService.Connection))
                {
                    var authorsParam = cmd.Parameters.Add("p_authors", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    authorsParam.Value = authors == null || authors.Length == 0 ? DBNull.Value : authors;

                    var genresParam = cmd.Parameters.Add("p_genres", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    genresParam.Value = genres == null || genres.Length == 0 ? DBNull.Value : genres;

                    var typeParam = cmd.Parameters.Add("p_type", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    typeParam.Value = types == null || types.Length == 0 ? DBNull.Value : types;

                    var excludedAuthorsParam = cmd.Parameters.Add("p_excluded_authors", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    excludedAuthorsParam.Value = excludedAuthors == null || excludedAuthors.Length == 0 ? DBNull.Value : excludedAuthors;

                    var excludedGenresParam = cmd.Parameters.Add("p_excluded_genres", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    excludedGenresParam.Value = excludedGenres == null || excludedGenres.Length == 0 ? DBNull.Value : excludedGenres;

                    var excludedTypesParam = cmd.Parameters.Add("p_excluded_type", NpgsqlDbType.Array | NpgsqlDbType.Text);
                    excludedTypesParam.Value = excludedTypes == null || excludedTypes.Length == 0 ? DBNull.Value : excludedTypes;

                    cmd.Parameters.AddWithValue("p_limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("p_offset", offset);
                    cmd.Parameters.AddWithValue("p_sort_by", sortBy.ToString());

                    var queryParam = cmd.Parameters.Add("p_query", NpgsqlDbType.Text);
                    queryParam.Value = string.IsNullOrWhiteSpace(query) ? DBNull.Value : query;

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(reader.GetOrdinal("id")),
                                Title = reader.IsDBNull(reader.GetOrdinal("title")) ? "" : reader.GetString(reader.GetOrdinal("title")),
                                Cover = reader.IsDBNull(reader.GetOrdinal("cover")) ? "" : reader.GetString(reader.GetOrdinal("cover")),
                                Description = reader.IsDBNull(reader.GetOrdinal("description")) ? "" : reader.GetString(reader.GetOrdinal("description")),
                                Status = reader.IsDBNull(reader.GetOrdinal("status")) ? "" : reader.GetString(reader.GetOrdinal("status")),
                                Type = reader.IsDBNull(reader.GetOrdinal("type")) ? MangaType.Manga : Enum.Parse<MangaType>(reader.GetString(reader.GetOrdinal("type"))),
                                Authors = reader.IsDBNull(reader.GetOrdinal("authors")) ? Array.Empty<string>() : (string[])reader.GetValue(reader.GetOrdinal("authors")),
                                Genres = reader.IsDBNull(reader.GetOrdinal("genres")) ? Array.Empty<string>() : (string[])reader.GetValue(reader.GetOrdinal("genres")),
                                Views = reader.IsDBNull(reader.GetOrdinal("view_count")) ? 0 : (int)reader.GetInt64(reader.GetOrdinal("view_count")),
                                Score = reader.IsDBNull(reader.GetOrdinal("score")) ? 0m : reader.GetDecimal(reader.GetOrdinal("score")),
                                AlternativeTitles = reader.IsDBNull(reader.GetOrdinal("alternative_titles")) ? null : (string[])reader.GetValue(reader.GetOrdinal("alternative_titles")),
                                MalId = reader.IsDBNull(reader.GetOrdinal("mal_id")) ? null : (int?)reader.GetInt64(reader.GetOrdinal("mal_id")),
                                AniId = reader.IsDBNull(reader.GetOrdinal("ani_id")) ? null : (int?)reader.GetInt64(reader.GetOrdinal("ani_id")),
                                CreatedAt = reader.IsDBNull(reader.GetOrdinal("created_at")) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("created_at"))),
                                UpdatedAt = reader.IsDBNull(reader.GetOrdinal("updated_at")) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("updated_at"))),
                            };
                            mangaList.Add(manga);
                            if (totalCount == 0)
                            {
                                totalCount = reader.GetInt64(reader.GetOrdinal("total_count"));
                            }
                        }
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

                var mangaList = new List<MangaResponse>();
                long totalCount = 0;

                using (var cmd = new NpgsqlCommand(popularQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_days", days);
                    cmd.Parameters.AddWithValue("p_limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("p_offset", offset);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                MalId = reader.IsDBNull(10) ? null : (int?)reader.GetInt64(10),
                                AniId = reader.IsDBNull(11) ? null : (int?)reader.GetInt64(11),
                                CreatedAt = reader.GetDateTime(12),
                                UpdatedAt = reader.GetDateTime(13),
                                AlternativeTitles = reader.IsDBNull(14) ? null : (string[])reader.GetValue(14),
                                Score = reader.GetDecimal(15),
                                Views = reader.GetInt64(16) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(16)
                            };
                            mangaList.Add(manga);

                            if (totalCount == 0)
                            {
                                totalCount = reader.GetInt64(17);
                            }
                        }
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
                    SELECT m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count, m.score, m.mal_id, m.ani_id, m.created_at, m.updated_at, m.alternative_titles
                    FROM manga m
                    WHERE m.id = @id";
                MangaResponse? manga = null;
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Cover = reader.GetString(2),
                                Description = reader.GetString(3),
                                Status = reader.GetString(4),
                                Type = Enum.Parse<MangaType>(reader.GetString(5)),
                                Authors = (string[])reader.GetValue(6),
                                Genres = (string[])reader.GetValue(7),
                                Views = reader.GetInt64(8) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(8),
                                Score = reader.GetDecimal(9),
                                MalId = reader.IsDBNull(10) ? null : (reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(10)),
                                AniId = reader.IsDBNull(11) ? null : (reader.GetInt64(11) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(11)),
                                CreatedAt = reader.GetDateTime(12),
                                UpdatedAt = reader.GetDateTime(13),
                                AlternativeTitles = reader.IsDBNull(14) ? null : (string[])reader.GetValue(14)
                            };
                        }
                    }
                }

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
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            manga = new MangaDetailResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Cover = reader.GetString(2),
                                Description = reader.GetString(3),
                                Status = reader.GetString(4),
                                Type = Enum.Parse<MangaType>(reader.GetString(5)),
                                Authors = (string[])reader.GetValue(6),
                                Genres = (string[])reader.GetValue(7),
                                Views = reader.GetInt64(8) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(8),
                                Score = reader.GetDecimal(9),
                                MalId = reader.IsDBNull(10) ? null : (reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(10)),
                                AniId = reader.IsDBNull(11) ? null : (reader.GetInt64(11) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(11)),
                                CreatedAt = reader.GetDateTime(12),
                                UpdatedAt = reader.GetDateTime(13),
                                AlternativeTitles = reader.IsDBNull(14) ? null : (string[])reader.GetValue(14)
                            };

                            var chaptersJson = reader.IsDBNull(15) ? "[]" : reader.GetString(15);
                            try
                            {
                                chapters = JsonSerializer.Deserialize<List<MangaChapter>>(chaptersJson, _jsonOptions) ?? new List<MangaChapter>();
                            }
                            catch (JsonException)
                            {
                                chapters = new List<MangaChapter>();
                            }
                        }
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

                var query = "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @id ORDER BY number DESC";
                var chapters = new List<MangaChapter>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var pagesValue = reader.GetValue(3);
                            short pagesCount;
                            if (pagesValue is short[] pagesArray)
                            {
                                pagesCount = (short)pagesArray.Length;
                            }
                            else if (pagesValue is short singlePage)
                            {
                                pagesCount = singlePage;
                            }
                            else
                            {
                                pagesCount = 0;
                            }

                            chapters.Add(new MangaChapter
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Number = reader.GetFloat(2),
                                Pages = pagesCount,
                                UpdatedAt = reader.GetDateTime(4),
                                CreatedAt = reader.GetDateTime(5),
                            });
                        }
                    }
                }

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

                // Check if manga exists
                var existsQuery = "SELECT 1 FROM manga WHERE id = @id LIMIT 1";
                using (var cmd = new NpgsqlCommand(existsQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            await _postgresService.CloseAsync();
                            return NotFound(ErrorResponse.Create("Manga not found"));
                        }
                    }
                }

                var query = @"
                    SELECT m.id, m.title, m.cover, m.description, m.status, m.type, m.authors, m.genres, m.view_count, m.score, m.alternative_titles, m.mal_id, m.ani_id, m.created_at, m.updated_at
                    FROM manga_similarities ms
                    JOIN manga m ON ms.similar_manga_id = m.id
                    WHERE ms.manga_id = @mangaId
                    ORDER BY ms.hybrid_similarity_score DESC
                    LIMIT @limit
                ";
                var recommendations = new List<MangaResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("mangaId", id);
                    cmd.Parameters.AddWithValue("limit", limit);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Cover = reader.GetString(2),
                                Description = reader.GetString(3),
                                Status = reader.GetString(4),
                                Type = Enum.Parse<MangaType>(reader.GetString(5)),
                                Authors = reader.GetFieldValue<string[]>(6),
                                Genres = reader.GetFieldValue<string[]>(7),
                                Views = reader.GetInt32(8),
                                Score = reader.GetDecimal(9),
                                AlternativeTitles = reader.IsDBNull(10) ? null : reader.GetFieldValue<string[]>(10),
                                MalId = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                                AniId = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                                CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
                                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14)
                            };
                            recommendations.Add(manga);
                        }
                    }
                }

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

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // IPv4
            {
                var bytes = ip.GetAddressBytes();
                // Check private ranges: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
                return bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) // IPv6
            {
                var bytes = ip.GetAddressBytes();
                // Check for link-local (fe80::/10) and private (fc00::/7)
                return (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || // fe80::/10
                    (bytes[0] == 0xfc || bytes[0] == 0xfd); // fc00::/7
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

            // Acquire semaphore for this IP to prevent concurrent requests
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
          AND ip = @p_ip
          AND viewed_at > now() - interval '24 hours'
    ) AS is_recent
), ins AS (
    INSERT INTO public.manga_views (manga_id, ip, user_id)
    SELECT @p_manga_id, @p_ip, @p_user_id
    WHERE NOT (SELECT is_recent FROM recent)
    RETURNING 1
)
SELECT CASE
    WHEN (SELECT is_recent FROM recent) THEN 'ignored_recent_view'
    ELSE 'view_logged'
END;";
                    string? content;
                    using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("p_manga_id", id);
                        cmd.Parameters.AddWithValue("p_ip", ipAddress);
                        cmd.Parameters.AddWithValue("p_user_id", userId == null ? DBNull.Value : userId);
                        var result = await cmd.ExecuteScalarAsync();
                        content = result?.ToString();
                    }

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
        [RequireTokenRefresh]
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
    m.view_count,
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

                var mangaList = new List<MangaResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_user_id", userId);
                    cmd.Parameters.AddWithValue("p_limit", limit);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Cover = reader.IsDBNull(2) ? "" : reader.GetString(2),
                                Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                Status = reader.IsDBNull(4) ? "" : reader.GetString(4),
                                Type = reader.IsDBNull(5) ? MangaType.Manga : Enum.Parse<MangaType>(reader.GetString(5)),
                                Authors = reader.IsDBNull(6) ? Array.Empty<string>() : (string[])reader.GetValue(6),
                                Genres = reader.IsDBNull(7) ? Array.Empty<string>() : (string[])reader.GetValue(7),
                                Views = reader.IsDBNull(8) ? 0 : (int)reader.GetInt64(8),
                                Score = reader.IsDBNull(9) ? 0m : reader.GetDecimal(9),
                                AlternativeTitles = reader.IsDBNull(10) ? null : (string[])reader.GetValue(10),
                                MalId = reader.IsDBNull(11) ? null : (int?)reader.GetInt64(11),
                                AniId = reader.IsDBNull(12) ? null : (int?)reader.GetInt64(12),
                                CreatedAt = reader.IsDBNull(13) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(13)),
                                UpdatedAt = reader.IsDBNull(14) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(14)),
                            };
                            mangaList.Add(manga);
                        }
                    }
                }

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
                await _postgresService.OpenAsync();

                var query = "INSERT INTO manga_ratings (user_id, manga_id, rating) VALUES (@userId, @mangaId, @rating) ON CONFLICT (user_id, manga_id) DO UPDATE SET rating = EXCLUDED.rating";
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("mangaId", id);
                    cmd.Parameters.AddWithValue("rating", request.Rating);
                    await cmd.ExecuteNonQueryAsync();
                }

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
        [RequireTokenRefresh]
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

                var query = "SELECT rating FROM manga_ratings WHERE user_id = @userId AND manga_id = @mangaId";
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("mangaId", id);
                    var result = await cmd.ExecuteScalarAsync();
                    await _postgresService.CloseAsync();

                    int? rating = result != null ? (int?)Convert.ToInt32(result) : null;
                    return Ok(SuccessResponse<int?>.Create(rating));
                }
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
        [RequireTokenRefresh]
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

                var query = "DELETE FROM manga_ratings WHERE user_id = @userId AND manga_id = @mangaId";
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("mangaId", id);
                    await cmd.ExecuteNonQueryAsync();
                }

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

                // Get manga
                var mangaQuery = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE mal_id = @id";
                MangaDetailResponse? manga = null;
                using (var cmd = new NpgsqlCommand(mangaQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            manga = new MangaDetailResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                Views = reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(10),
                                Score = reader.GetDecimal(11),
                                MalId = reader.IsDBNull(12) ? null : (reader.GetInt64(12) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(12)),
                                AniId = reader.IsDBNull(13) ? null : (reader.GetInt64(13) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(13)),
                                CreatedAt = reader.GetDateTime(14),
                                UpdatedAt = reader.GetDateTime(15),
                                AlternativeTitles = reader.IsDBNull(16) ? null : (string[])reader.GetValue(16)
                            };
                        }
                    }
                }

                if (manga == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                // Get chapters
                var chaptersQuery = "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @mangaId ORDER BY number";
                var chapters = new List<MangaChapter>();
                using (var cmd = new NpgsqlCommand(chaptersQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("mangaId", manga.Id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var pagesValue = reader.GetValue(3);
                            short pagesCount;
                            if (pagesValue is short[] pagesArray)
                            {
                                pagesCount = (short)pagesArray.Length;
                            }
                            else if (pagesValue is short singlePage)
                            {
                                pagesCount = singlePage;
                            }
                            else
                            {
                                pagesCount = 0;
                            }

                            chapters.Add(new MangaChapter
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Number = reader.GetFloat(2),
                                Pages = pagesCount,
                                UpdatedAt = reader.GetDateTime(4),
                                CreatedAt = reader.GetDateTime(5)
                            });
                        }
                    }
                }

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

                var query = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE mal_id = ANY(@malIds)";
                var mangaList = new List<MangaResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("malIds", request.MalIds);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                Views = reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(10),
                                Score = reader.GetDecimal(11),
                                MalId = reader.IsDBNull(12) ? null : (reader.GetInt64(12) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(12)),
                                AniId = reader.IsDBNull(13) ? null : (reader.GetInt64(13) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(13)),
                                CreatedAt = reader.GetDateTime(14),
                                UpdatedAt = reader.GetDateTime(15),
                                AlternativeTitles = reader.IsDBNull(16) ? null : (string[])reader.GetValue(16)
                            };
                            mangaList.Add(manga);
                        }
                    }
                }

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

                // Get manga
                var mangaQuery = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE ani_id = @id";
                MangaDetailResponse? manga = null;
                using (var cmd = new NpgsqlCommand(mangaQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            manga = new MangaDetailResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                Views = reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(10),
                                Score = reader.GetDecimal(11),
                                MalId = reader.IsDBNull(12) ? null : (reader.GetInt64(12) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(12)),
                                AniId = reader.IsDBNull(13) ? null : (reader.GetInt64(13) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(13)),
                                CreatedAt = reader.GetDateTime(14),
                                UpdatedAt = reader.GetDateTime(15),
                                AlternativeTitles = reader.IsDBNull(16) ? null : (string[])reader.GetValue(16)
                            };
                        }
                    }
                }

                if (manga == null)
                    return NotFound(ErrorResponse.Create("Manga not found", status: 404));

                // Get chapters
                var chaptersQuery = "SELECT id, title, number, pages, updated_at, created_at FROM chapters WHERE manga_id = @mangaId ORDER BY number";
                var chapters = new List<MangaChapter>();
                using (var cmd = new NpgsqlCommand(chaptersQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("mangaId", manga.Id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var pagesValue = reader.GetValue(3);
                            short pagesCount;
                            if (pagesValue is short[] pagesArray)
                            {
                                pagesCount = (short)pagesArray.Length;
                            }
                            else if (pagesValue is short singlePage)
                            {
                                pagesCount = singlePage;
                            }
                            else
                            {
                                pagesCount = 0;
                            }

                            chapters.Add(new MangaChapter
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Number = reader.GetFloat(2),
                                Pages = pagesCount,
                                UpdatedAt = reader.GetDateTime(4),
                                CreatedAt = reader.GetDateTime(5)
                            });
                        }
                    }
                }

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

                var query = "SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles FROM manga WHERE ani_id = ANY(@aniIds)";
                var mangaList = new List<MangaResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("aniIds", request.AniIds);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                Views = reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(10),
                                Score = reader.GetDecimal(11),
                                MalId = reader.IsDBNull(12) ? null : (reader.GetInt64(12) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(12)),
                                AniId = reader.IsDBNull(13) ? null : (reader.GetInt64(13) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(13)),
                                CreatedAt = reader.GetDateTime(14),
                                UpdatedAt = reader.GetDateTime(15),
                                AlternativeTitles = reader.IsDBNull(16) ? null : (string[])reader.GetValue(16)
                            };
                            mangaList.Add(manga);
                        }
                    }
                }

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
                string? content;
                using (var cmd = new NpgsqlCommand(rpcQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("_manga_id", id);
                    cmd.Parameters.AddWithValue("_number", subId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            content = reader.IsDBNull(0) ? null : reader.GetString(0);
                        }
                        else
                        {
                            content = null;
                        }
                    }
                }

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
                    SELECT id, title, cover, description, status, type, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles
                    FROM manga
                    WHERE search_vector @@ plainto_tsquery('english', @query)
                    ORDER BY (ts_rank(search_vector, plainto_tsquery('english', @query)) + (view_count::float / 100)) DESC
                    LIMIT @limit";
                var mangaList = new List<MangaSearchResponse>();
                using (var cmd = new NpgsqlCommand(searchQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("query", query);
                    cmd.Parameters.AddWithValue("limit", limit);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaSearchResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(1),
                                Cover = reader.GetString(2),
                                Description = reader.GetString(3),
                                Status = reader.GetString(4),
                                Type = Enum.Parse<MangaType>(reader.GetString(5)),
                                Authors = (string[])reader.GetValue(6),
                                Genres = (string[])reader.GetValue(7),
                                Views = reader.GetInt64(8) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(8),
                                Score = reader.GetDecimal(9),
                                MalId = reader.IsDBNull(10) ? null : (reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(10)),
                                AniId = reader.IsDBNull(11) ? null : (reader.GetInt64(11) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(11)),
                                CreatedAt = reader.GetDateTime(12),
                                UpdatedAt = reader.GetDateTime(13),
                                AlternativeTitles = reader.IsDBNull(14) ? null : (string[])reader.GetValue(14)
                            };
                            mangaList.Add(manga);
                        }
                    }
                }

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

                var query = "SELECT id, COUNT(*) OVER() as total FROM manga ORDER BY view_count DESC LIMIT @limit OFFSET @offset";
                var ids = new List<Guid>();
                long totalCount = 0;
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            ids.Add(reader.GetGuid(0));
                            if (totalCount == 0)
                            {
                                totalCount = reader.GetInt64(1);
                            }
                        }
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
                var pairs = new List<MangaChapterIdsPair>();
                long totalCount = 0;
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var mangaId = reader.GetGuid(0);
                            var chapterNumbers = reader.IsDBNull(1) ? Array.Empty<float>() : (float[])reader.GetValue(1);
                            if (totalCount == 0)
                            {
                                totalCount = reader.GetInt64(2);
                            }
                            pairs.Add(new MangaChapterIdsPair
                            {
                                MangaId = mangaId,
                                ChapterIds = chapterNumbers.ToList()
                            });
                        }
                    }
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