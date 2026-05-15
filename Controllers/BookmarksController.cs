using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Dapper;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/bookmarks")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class BookmarksController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;

        public BookmarksController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        private static short GetPagesCount(object? pagesVal)
        {
            if (pagesVal is short[] arr) return (short)arr.Length;
            if (pagesVal is short s) return s;
            if (pagesVal == null) return 0;
            return (short)0;
        }

        private static int ToInt32Clamp(object? value)
        {
            if (value == null)
            {
                return 0;
            }

            var longValue = Convert.ToInt64(value);
            if (longValue > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (longValue < int.MinValue)
            {
                return int.MinValue;
            }

            return (int)longValue;
        }

        private static long ToInt64(object? value)
        {
            return value == null ? 0L : Convert.ToInt64(value);
        }

        private static int? ToNullableInt(object? value)
        {
            if (value == null)
            {
                return null;
            }

            return ToInt32Clamp(value);
        }

        private static MangaChapter MapBookmarkChapter(dynamic row, string idField, string numberField, string titleField, string pagesField, string scanlatorIdField, string createdAtField, string updatedAtField)
        {
            var values = (IDictionary<string, object?>)row;
            var pagesValue = values[pagesField];

            return new MangaChapter
            {
                Id = (Guid)values[idField]!,
                Number = (float)values[numberField]!,
                Title = values[titleField] == null ? string.Empty : (string)values[titleField]!,
                Pages = GetPagesCount(pagesValue),
                ScanlatorId = ToInt32Clamp(values[scanlatorIdField]),
                CreatedAt = values[createdAtField] == null ? DateTimeOffset.MinValue : (DateTime)values[createdAtField]!,
                UpdatedAt = values[updatedAtField] == null ? DateTimeOffset.MinValue : (DateTime)values[updatedAtField]!,
            };
        }

        private static BookmarkResponse MapBookmarkRow(dynamic r, ref long totalCount)
        {
            var bookmark = new BookmarkResponse
            {
                BookmarkId = (Guid)r.bookmark_id,
                BookmarkCreatedAt = (DateTime)r.bookmark_created_at,
                BookmarkUpdatedAt = (DateTime)r.bookmark_updated_at,
                MangaId = (Guid)r.manga_id,
                Title = (string)r.title,
                Cover = (string)r.cover,
                Description = (string)r.description,
                Status = (string)r.status,
                Type = Enum.Parse<MangaType>((string)r.type, true),
                Authors = (string[])r.authors,
                Genres = (string[])r.genres,
                Views = ToInt32Clamp(r.view_count),
                Score = (decimal)r.score,
                MalId = ToNullableInt(r.mal_id),
                AniId = ToNullableInt(r.ani_id),
                AlternativeTitles = r.alternative_titles == null ? Array.Empty<string>() : (string[])r.alternative_titles,
                MangaCreatedAt = (DateTime)r.manga_created_at,
                MangaUpdatedAt = (DateTime)r.manga_updated_at,
                ChaptersBehind = ToInt32Clamp(r.chapters_behind)
            };
            totalCount = ToInt64(r.total_count);
            if (r.lrc_id != null) bookmark.LastReadChapter = MapBookmarkChapter(r, "lrc_id", "lrc_number", "lrc_title", "lrc_pages", "lrc_scanlator_id", "lrc_created_at", "lrc_updated_at");
            if (r.latest_id != null) bookmark.LatestChapter = MapBookmarkChapter(r, "latest_id", "latest_number", "latest_title", "latest_pages", "latest_scanlator_id", "latest_created_at", "latest_updated_at");
            if (r.next_id != null) bookmark.NextChapter = MapBookmarkChapter(r, "next_id", "next_number", "next_title", "next_pages", "next_scanlator_id", "next_created_at", "next_updated_at");
            return bookmark;
        }

        /// <summary>
        /// Get bookmarks
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of the user's bookmarks.</returns>
        [HttpGet]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<BookmarkListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetBookmarks([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var offset = (clampedPage - 1) * clampedPageSize;

                var bookmarksQuery = @"
                    base_bookmarks AS (
                        SELECT
                            ub.id                AS bookmark_id,
                            ub.created_at        AS bookmark_created_at,
                            ub.updated_at        AS bookmark_updated_at,
                            ub.last_read_chapter_id,
                            m.id                 AS manga_id,
                            m.title,
                            m.cover,
                            m.description,
                            m.status,
                            m.type,
                            m.authors,
                            m.genres,
                            m.view_count,
                            m.score,
                            m.mal_id,
                            m.ani_id,
                            m.alternative_titles,
                            m.created_at         AS manga_created_at,
                            m.updated_at         AS manga_updated_at,
                            COUNT(*) OVER ()     AS total_count
                        FROM user_bookmarks ub
                        INNER JOIN manga m ON ub.manga_id = m.id
                        WHERE ub.user_id = @userId
                    ),
                    enriched AS MATERIALIZED (
                        SELECT
                            b.bookmark_id,
                            b.bookmark_created_at,
                            b.bookmark_updated_at,
                            b.last_read_chapter_id,
                            b.manga_id,
                            b.title,
                            b.cover,
                            b.description,
                            b.status,
                            b.type,
                            b.authors,
                            b.genres,
                            b.view_count,
                            b.score,
                            b.mal_id,
                            b.ani_id,
                            b.alternative_titles,
                            b.manga_created_at,
                            b.manga_updated_at,
                            b.total_count,
                            CASE
                                WHEN lrc.number IS NULL THEN (
                                    SELECT COUNT(*)
                                    FROM chapters c_count
                                    WHERE c_count.manga_id = b.manga_id
                                )
                                ELSE (
                                    SELECT COUNT(*)
                                    FROM chapters c_count
                                    WHERE c_count.manga_id = b.manga_id
                                    AND c_count.number > lrc.number
                                    AND c_count.scanlator_id = lrc.scanlator_id
                                )
                            END                  AS chapters_behind,
                            lrc.id               AS lrc_id,
                            lrc.number           AS lrc_number,
                            lrc.title            AS lrc_title,
                            lrc.pages            AS lrc_pages,
                            lrc.scanlator_id     AS lrc_scanlator_id,
                            lrc.created_at       AS lrc_created_at,
                            lrc.updated_at       AS lrc_updated_at,
                            latest.id            AS latest_id,
                            latest.number        AS latest_number,
                            latest.title         AS latest_title,
                            latest.pages         AS latest_pages,
                            latest.scanlator_id  AS latest_scanlator_id,
                            latest.created_at    AS latest_created_at,
                            latest.updated_at    AS latest_updated_at,
                            COALESCE(next_ch.id,           latest.id)           AS next_id,
                            COALESCE(next_ch.number,       latest.number)       AS next_number,
                            COALESCE(next_ch.title,        latest.title)        AS next_title,
                            COALESCE(next_ch.pages,        latest.pages)        AS next_pages,
                            COALESCE(next_ch.scanlator_id, latest.scanlator_id) AS next_scanlator_id,
                            COALESCE(next_ch.created_at,   latest.created_at)   AS next_created_at,
                            COALESCE(next_ch.updated_at,   latest.updated_at)   AS next_updated_at
                        FROM base_bookmarks b
                        LEFT JOIN chapters lrc
                            ON lrc.id = b.last_read_chapter_id
                        LEFT JOIN LATERAL (
                            SELECT id, number, title, pages, scanlator_id, created_at, updated_at
                            FROM chapters
                            WHERE manga_id = b.manga_id
                            AND (lrc.scanlator_id IS NULL OR scanlator_id = lrc.scanlator_id)
                            ORDER BY number DESC, created_at DESC
                            LIMIT 1
                        ) latest ON true
                        LEFT JOIN LATERAL (
                            SELECT id, number, title, pages, scanlator_id, created_at, updated_at
                            FROM chapters
                            WHERE manga_id = b.manga_id
                            AND (lrc.scanlator_id IS NULL OR scanlator_id = lrc.scanlator_id)
                            AND number > COALESCE(lrc.number, -1)
                            ORDER BY number ASC
                            LIMIT 1
                        ) next_ch ON true
                    )
                    SELECT *
                    FROM enriched
                    ORDER BY
                        (chapters_behind > 0) DESC,
                        COALESCE(latest_created_at, bookmark_updated_at) DESC
                    LIMIT @limit OFFSET @offset";

                var bookmarks = new List<BookmarkResponse>();
                long totalCount = 0;

                var rows = await _postgresService.Connection.QueryAsync(bookmarksQuery, new { userId, limit = clampedPageSize, offset });
                foreach (var r in rows)
                {
                    bookmarks.Add(MapBookmarkRow(r, ref totalCount));
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                {
                    Items = bookmarks,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve bookmarks", ex.Message));
            }
        }

        /// <summary>
        /// Search user's bookmarks
        /// </summary>
        /// <param name="query">The search query to filter bookmarks by manga title.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of matching bookmarked manga.</returns>
        [HttpGet("search")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<BookmarkListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SearchBookmarks([FromQuery, Required] string query, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(ErrorResponse.Create("Bad Request", "Search query is required", 400));
            }

            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var offset = (clampedPage - 1) * clampedPageSize;

                var searchQuery = @"
                    SELECT
                        ub.id as bookmark_id,
                        ub.created_at as bookmark_created_at,
                        ub.updated_at as bookmark_updated_at,
                        m.id as manga_id,
                        m.title,
                        m.cover,
                        m.description,
                        m.status,
                        m.type,
                        m.authors,
                        m.genres,
                        m.view_count,
                        m.score,
                        m.mal_id,
                        m.ani_id,
                        m.alternative_titles,
                        m.created_at as manga_created_at,
                        m.updated_at as manga_updated_at,
                        COUNT(*) OVER() as total_count,
                        CASE
                            WHEN lrc.number IS NULL THEN (
                                SELECT COUNT(*)
                                FROM chapters c
                                WHERE c.manga_id = m.id
                            )
                            ELSE (
                                SELECT COUNT(*)
                                FROM chapters c
                                WHERE c.manga_id = m.id
                                  AND c.scanlator_id = lrc.scanlator_id
                                  AND c.number > lrc.number
                            )
                        END as chapters_behind,
                        lrc.id as lrc_id,
                        lrc.number as lrc_number,
                        lrc.title as lrc_title,
                        lrc.pages as lrc_pages,
                        lrc.scanlator_id as lrc_scanlator_id,
                        lrc.created_at as lrc_created_at,
                        lrc.updated_at as lrc_updated_at,
                        latest.id as latest_id,
                        latest.number as latest_number,
                        latest.title as latest_title,
                        latest.pages as latest_pages,
                        latest.scanlator_id as latest_scanlator_id,
                        latest.created_at as latest_created_at,
                        latest.updated_at as latest_updated_at,
                        next.id as next_id,
                        next.number as next_number,
                        next.title as next_title,
                        next.pages as next_pages,
                        next.scanlator_id as next_scanlator_id,
                        next.created_at as next_created_at,
                        next.updated_at as next_updated_at
                    FROM user_bookmarks ub
                    INNER JOIN manga m ON ub.manga_id = m.id
                    LEFT JOIN chapters lrc ON lrc.id = ub.last_read_chapter_id
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, scanlator_id, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = m.id
                          AND (lrc.scanlator_id IS NULL OR scanlator_id = lrc.scanlator_id)
                        ORDER BY number DESC, created_at DESC
                        LIMIT 1
                    ) latest ON true
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, scanlator_id, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = m.id
                        AND (lrc.scanlator_id IS NULL OR scanlator_id = lrc.scanlator_id)
                        AND (
                            CASE
                                WHEN lrc.number IS NULL OR lrc.number = (SELECT MIN(number) FROM chapters WHERE manga_id = m.id)
                                THEN number = (SELECT MIN(number) FROM chapters WHERE manga_id = m.id)
                                WHEN lrc.number >= (SELECT MAX(number) FROM chapters WHERE manga_id = m.id)
                                THEN number = (SELECT MAX(number) FROM chapters WHERE manga_id = m.id)
                                ELSE number > lrc.number
                            END
                        )
                        ORDER BY number ASC, created_at ASC
                        LIMIT 1
                    ) next ON true
                    WHERE ub.user_id = @userId
                    AND m.search_vector @@ websearch_to_tsquery('english', @query)
                    ORDER BY ts_rank(m.search_vector, websearch_to_tsquery('english', @query)) DESC
                    LIMIT @limit OFFSET @offset";

                var bookmarks = new List<BookmarkResponse>();
                long totalCount = 0;

                var rows = await _postgresService.Connection.QueryAsync(searchQuery, new { userId, query, limit = clampedPageSize, offset });
                foreach (var r in rows)
                {
                    bookmarks.Add(MapBookmarkRow(r, ref totalCount));
                }

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                {
                    Items = bookmarks,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to search bookmarks", ex.Message));
            }
        }

        /// <summary>
        /// Get unread bookmarks count
        /// </summary>
        /// <returns>The number of unread bookmarked manga.</returns>
        [HttpGet("unread")]
        [CacheControl(CacheDuration.OneMinute, CacheDuration.OneMinute, false)]
        [ProducesResponseType(typeof(SuccessResponse<int>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUnreadBookmarksCount()
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var count = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM user_bookmarks_unread WHERE user_id = @userId",
                    new { userId });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<int>.Create((int)count));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve unread bookmarks count", ex.Message));
            }
        }

        /// <summary>
        /// Update bookmark
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <param name="request">The update request containing chapter number.</param>
        /// <returns>Success message.</returns>
        [HttpPut("{mangaId}")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateReadChapter(Guid mangaId, [FromBody] UpdateBookmarkRequest request)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                Guid? chapterId = null;
                string chapterQuery;

                if (request.ChapterNumber.HasValue)
                {
                    chapterQuery = "SELECT id FROM chapters WHERE manga_id = @mangaId AND ABS(number - @chapterNumber) < 0.0001 LIMIT 1";
                }
                else
                {
                    chapterQuery = "SELECT id FROM chapters WHERE manga_id = @mangaId ORDER BY number ASC LIMIT 1";
                }

                chapterId = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(chapterQuery, new
                {
                    mangaId,
                    chapterNumber = request.ChapterNumber.HasValue ? request.ChapterNumber.Value : (double?)null
                });

                if (chapterId == null)
                {
                    await _postgresService.CloseAsync();
                    return BadRequest(ErrorResponse.Create("Bad Request", request.ChapterNumber.HasValue
                        ? $"Chapter {request.ChapterNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} not found for this manga"
                        : "No chapters found for this manga", 400));
                }

                var upsertQuery = @"
                    INSERT INTO user_bookmarks (user_id, manga_id, last_read_chapter_id, updated_at, created_at)
                    VALUES (@userId, @mangaId, @chapterId, NOW(), NOW())
                    ON CONFLICT (user_id, manga_id)
                    DO UPDATE SET last_read_chapter_id = @chapterId, updated_at = NOW()";

                await _postgresService.Connection.ExecuteAsync(upsertQuery, new { userId, mangaId, chapterId = chapterId.Value });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Bookmark updated successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to update bookmark", ex.Message));
            }
        }

        /// <summary>
        /// Get last read chapter
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>The last read chapter details.</returns>
        [HttpGet("{mangaId}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<LastReadResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetLastReadChapter(Guid mangaId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var query = @"
                    SELECT c.id, c.number, c.title, c.pages, c.scanlator_id, c.created_at, c.updated_at
                    FROM user_bookmarks ub
                    INNER JOIN chapters c ON ub.last_read_chapter_id = c.id
                    WHERE ub.user_id = @userId AND ub.manga_id = @mangaId";

                var row = await _postgresService.Connection.QueryFirstOrDefaultAsync(query, new { userId, mangaId });

                if (row == null)
                {
                    await _postgresService.CloseAsync();
                    return Ok(SuccessResponse<LastReadResponse>.Create(null!));
                }

                var response = new LastReadResponse
                {
                    Id = (Guid)row.id,
                    Number = (float)row.number,
                    Title = (string)row.title,
                    Pages = Convert.ToInt16(row.pages),
                    ScanlatorId = ToInt32Clamp(row.scanlator_id),
                    CreatedAt = (DateTime)row.created_at,
                    UpdatedAt = (DateTime)row.updated_at
                };

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<LastReadResponse>.Create(response));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve last read chapter", ex.Message));
            }
        }

        /// <summary>
        /// Delete bookmark
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>Success message.</returns>
        [HttpDelete("{mangaId}")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> DeleteBookmark(Guid mangaId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var existing = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM user_bookmarks WHERE user_id = @userId AND manga_id = @mangaId",
                    new { userId, mangaId });

                if (existing == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not Found", "Bookmark not found", 404));
                }

                await _postgresService.Connection.ExecuteAsync(
                    "DELETE FROM user_bookmarks WHERE user_id = @userId AND manga_id = @mangaId",
                    new { userId, mangaId });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Bookmark deleted successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to delete bookmark", ex.Message));
            }
        }

        /// <summary>
        /// Batch update bookmarks
        /// </summary>
        /// <param name="request">The batch update request.</param>
        /// <returns>Success message.</returns>
        [HttpPost("batch")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> BatchUpdateBookmarks([FromBody] BatchUpdateBookmarksRequest request)
        {
            const int maxBatchSize = 100;
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest(ErrorResponse.Create("Bad Request", "No items provided", 400));
            }
            if (request.Items.Count > maxBatchSize)
            {
                return BadRequest(ErrorResponse.Create("Bad Request", $"Batch size exceeds maximum of {maxBatchSize}", 400));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
                }

                await _postgresService.OpenAsync();

                var batchQuery = @"
                    WITH input_data AS (
                        SELECT
                            unnest(@mangaIds::uuid[]) as manga_id,
                            unnest(@chapterNumbers::double precision[]) as chapter_number
                    ),
                    resolved_chapters AS (
                        SELECT
                            i.manga_id,
                            CASE
                                WHEN i.chapter_number = 0 THEN (
                                    SELECT id
                                    FROM chapters
                                    WHERE manga_id = i.manga_id
                                    ORDER BY number ASC
                                    LIMIT 1
                                )
                                ELSE (
                                    SELECT id
                                    FROM chapters
                                    WHERE manga_id = i.manga_id
                                    ORDER BY ABS(number - i.chapter_number) ASC, number ASC
                                    LIMIT 1
                                )
                            END as chapter_id
                        FROM input_data i
                    )
                    INSERT INTO user_bookmarks (user_id, manga_id, last_read_chapter_id, updated_at, created_at)
                    SELECT @userId, manga_id, chapter_id, NOW(), NOW()
                    FROM resolved_chapters
                    WHERE chapter_id IS NOT NULL
                    ON CONFLICT (user_id, manga_id)
                    DO UPDATE SET
                        last_read_chapter_id = EXCLUDED.last_read_chapter_id,
                        updated_at = NOW()";

                await _postgresService.Connection.ExecuteAsync(batchQuery, new
                {
                    userId,
                    mangaIds = request.Items.Select(i => i.MangaId).ToArray(),
                    chapterNumbers = request.Items.Select(i => (double)i.ChapterNumber).ToArray()
                });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Bookmarks updated successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                if (ex.Message.Contains("Chapter not found") || ex.Message.Contains("Array lengths"))
                {
                    return BadRequest(ErrorResponse.Create("Bad Request", ex.Message, 400));
                }
                return StatusCode(500, ErrorResponse.Create("Failed to batch update bookmarks", ex.Message));
            }
        }
    }
}
