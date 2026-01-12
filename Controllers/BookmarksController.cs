using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Npgsql;
using System.Linq;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/bookmarks")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    [RequireTokenRefresh]
    public class BookmarksController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;

        public BookmarksController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        /// <summary>
        /// Get bookmarks
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of the user's bookmarks.</returns>
        [HttpGet]
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
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
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                var offset = (clampedPage - 1) * clampedPageSize;

                // Get everything in a single query with LATERAL joins
                var bookmarksQuery = @"
                    WITH base_bookmarks AS (
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
                            ub.last_read_chapter_id,
                            COUNT(*) OVER() as total_count
                        FROM user_bookmarks ub
                        INNER JOIN manga m ON ub.manga_id = m.id
                        WHERE ub.user_id = @userId
                    ),
                    sorted_bookmarks AS (
                        SELECT
                            b.*,
                            COALESCE(
                                (SELECT COUNT(*)
                                 FROM chapters c
                                 WHERE c.manga_id = b.manga_id
                                 AND c.number > COALESCE((SELECT number FROM chapters WHERE id = b.last_read_chapter_id), 0)),
                                (SELECT COUNT(*) FROM chapters WHERE manga_id = b.manga_id)
                            ) as chapters_behind
                        FROM base_bookmarks b
                    )
                    SELECT
                        sb.*,
                        lrc.id as lrc_id,
                        lrc.number as lrc_number,
                        lrc.title as lrc_title,
                        lrc.pages as lrc_pages,
                        lrc.created_at as lrc_created_at,
                        lrc.updated_at as lrc_updated_at,
                        latest.id as latest_id,
                        latest.number as latest_number,
                        latest.title as latest_title,
                        latest.pages as latest_pages,
                        latest.created_at as latest_created_at,
                        latest.updated_at as latest_updated_at,
                        next.id as next_id,
                        next.number as next_number,
                        next.title as next_title,
                        next.pages as next_pages,
                        next.created_at as next_created_at,
                        next.updated_at as next_updated_at
                    FROM sorted_bookmarks sb
                    LEFT JOIN chapters lrc ON lrc.id = sb.last_read_chapter_id
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = sb.manga_id
                        ORDER BY number DESC, created_at DESC
                        LIMIT 1
                    ) latest ON true
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = sb.manga_id
                        AND (
                            CASE
                                WHEN lrc.number IS NULL OR lrc.number = (SELECT MIN(number) FROM chapters WHERE manga_id = sb.manga_id)
                                THEN number = (SELECT MIN(number) FROM chapters WHERE manga_id = sb.manga_id)
                                WHEN lrc.number >= (SELECT MAX(number) FROM chapters WHERE manga_id = sb.manga_id)
                                THEN number = (SELECT MAX(number) FROM chapters WHERE manga_id = sb.manga_id)
                                ELSE number > lrc.number
                            END
                        )
                        ORDER BY number ASC, created_at ASC
                        LIMIT 1
                    ) next ON true
                    ORDER BY (sb.chapters_behind > 0) DESC, sb.manga_updated_at DESC
                    LIMIT @limit OFFSET @offset";

                var bookmarks = new List<BookmarkResponse>();
                long totalCount = 0;

                using (var cmd = new NpgsqlCommand(bookmarksQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("offset", offset);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var bookmarkId = reader.GetGuid(0);
                            var bookmark = new BookmarkResponse
                            {
                                BookmarkId = bookmarkId,
                                BookmarkCreatedAt = reader.GetDateTime(1),
                                BookmarkUpdatedAt = reader.GetDateTime(2),
                                MangaId = reader.GetGuid(3),
                                Title = reader.GetString(4),
                                Cover = reader.GetString(5),
                                Description = reader.GetString(6),
                                Status = reader.GetString(7),
                                Type = Enum.Parse<MangaType>(reader.GetString(8), true),
                                Authors = (string[])reader.GetValue(9),
                                Genres = (string[])reader.GetValue(10),
                                Views = (int)(long)reader.GetValue(11),
                                Score = reader.GetDecimal(12),
                                MalId = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                                AniId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                                AlternativeTitles = (string[])reader.GetValue(15),
                                MangaCreatedAt = reader.GetDateTime(16),
                                MangaUpdatedAt = reader.GetDateTime(17)
                            };

                        totalCount = reader.GetInt64(19);
                        bookmark.ChaptersBehind = reader.GetInt32(20);

                        // Last read chapter
                        if (!reader.IsDBNull(21))
                        {
                            bookmark.LastReadChapter = new BookmarkChapter
                            {
                                Id = reader.GetGuid(21),
                                Number = reader.GetFloat(22),
                                Title = reader.IsDBNull(23) ? string.Empty : reader.GetString(23),
                                Pages = reader.IsDBNull(24) ? (short)0 : reader.GetInt16(24),
                                CreatedAt = reader.IsDBNull(25) ? DateTimeOffset.MinValue : reader.GetDateTime(25),
                                UpdatedAt = reader.IsDBNull(26) ? DateTimeOffset.MinValue : reader.GetDateTime(26)
                            };
                        }

                        // Latest chapter
                        if (!reader.IsDBNull(27))
                        {
                            bookmark.LatestChapter = new BookmarkChapter
                            {
                                Id = reader.GetGuid(27),
                                Number = reader.GetFloat(28),
                                Title = reader.IsDBNull(29) ? string.Empty : reader.GetString(29),
                                Pages = reader.IsDBNull(30) ? (short)0 : reader.GetInt16(30),
                                CreatedAt = reader.IsDBNull(31) ? DateTimeOffset.MinValue : reader.GetDateTime(31),
                                UpdatedAt = reader.IsDBNull(32) ? DateTimeOffset.MinValue : reader.GetDateTime(32)
                            };
                        }

                        // Next chapter
                        if (!reader.IsDBNull(33))
                        {
                            bookmark.NextChapter = new BookmarkChapter
                            {
                                Id = reader.GetGuid(33),
                                Number = reader.GetFloat(34),
                                Title = reader.IsDBNull(35) ? string.Empty : reader.GetString(35),
                                Pages = reader.IsDBNull(36) ? (short)0 : reader.GetInt16(36),
                                CreatedAt = reader.IsDBNull(37) ? DateTimeOffset.MinValue : reader.GetDateTime(37),
                                UpdatedAt = reader.IsDBNull(38) ? DateTimeOffset.MinValue : reader.GetDateTime(38)
                            };
                        }

                            bookmarks.Add(bookmark);
                        }
                    }
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
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(SuccessResponse<BookmarkListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SearchBookmarks([FromQuery, Required] string query, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(ErrorResponse.Create("Bad Request", "Search query is required"));
            }

            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                var offset = (clampedPage - 1) * clampedPageSize;

                // Get everything in a single query with LATERAL joins
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
                        COALESCE(
                            (SELECT COUNT(*)
                             FROM chapters c
                             WHERE c.manga_id = m.id
                             AND c.number > COALESCE((SELECT number FROM chapters WHERE id = ub.last_read_chapter_id), 0)),
                            (SELECT COUNT(*) FROM chapters WHERE manga_id = m.id)
                        ) as chapters_behind,
                        lrc.id as lrc_id,
                        lrc.number as lrc_number,
                        lrc.title as lrc_title,
                        lrc.pages as lrc_pages,
                        lrc.created_at as lrc_created_at,
                        lrc.updated_at as lrc_updated_at,
                        latest.id as latest_id,
                        latest.number as latest_number,
                        latest.title as latest_title,
                        latest.pages as latest_pages,
                        latest.created_at as latest_created_at,
                        latest.updated_at as latest_updated_at,
                        next.id as next_id,
                        next.number as next_number,
                        next.title as next_title,
                        next.pages as next_pages,
                        next.created_at as next_created_at,
                        next.updated_at as next_updated_at
                    FROM user_bookmarks ub
                    INNER JOIN manga m ON ub.manga_id = m.id
                    LEFT JOIN chapters lrc ON lrc.id = ub.last_read_chapter_id
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = m.id
                        ORDER BY number DESC, created_at DESC
                        LIMIT 1
                    ) latest ON true
                    LEFT JOIN LATERAL (
                        SELECT id, number, title, pages, created_at, updated_at
                        FROM chapters
                        WHERE manga_id = m.id
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

                using (var cmd = new NpgsqlCommand(searchQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("query", query);
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("offset", offset);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var bookmarkId = reader.GetGuid(0);
                            var bookmark = new BookmarkResponse
                            {
                                BookmarkId = bookmarkId,
                                BookmarkCreatedAt = reader.GetDateTime(1),
                                BookmarkUpdatedAt = reader.GetDateTime(2),
                                MangaId = reader.GetGuid(3),
                                Title = reader.GetString(4),
                                Cover = reader.GetString(5),
                                Description = reader.GetString(6),
                                Status = reader.GetString(7),
                                Type = Enum.Parse<MangaType>(reader.GetString(8), true),
                                Authors = (string[])reader.GetValue(9),
                                Genres = (string[])reader.GetValue(10),
                                Views = (int)(long)reader.GetValue(11),
                                Score = reader.GetDecimal(12),
                                MalId = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                                AniId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                                AlternativeTitles = (string[])reader.GetValue(15),
                                MangaCreatedAt = reader.GetDateTime(16),
                                MangaUpdatedAt = reader.GetDateTime(17)
                            };

                            totalCount = reader.GetInt64(18);
                            bookmark.ChaptersBehind = reader.GetInt32(19);

                            // Last read chapter
                            if (!reader.IsDBNull(20))
                            {
                                bookmark.LastReadChapter = new BookmarkChapter
                                {
                                    Id = reader.GetGuid(20),
                                    Number = reader.GetFloat(21),
                                    Title = reader.IsDBNull(22) ? string.Empty : reader.GetString(22),
                                    Pages = reader.IsDBNull(23) ? (short)0 : reader.GetInt16(23),
                                    CreatedAt = reader.IsDBNull(24) ? DateTimeOffset.MinValue : reader.GetDateTime(24),
                                    UpdatedAt = reader.IsDBNull(25) ? DateTimeOffset.MinValue : reader.GetDateTime(25)
                                };
                            }

                            // Latest chapter
                            if (!reader.IsDBNull(26))
                            {
                                bookmark.LatestChapter = new BookmarkChapter
                                {
                                    Id = reader.GetGuid(26),
                                    Number = reader.GetFloat(27),
                                    Title = reader.IsDBNull(28) ? string.Empty : reader.GetString(28),
                                    Pages = reader.IsDBNull(29) ? (short)0 : reader.GetInt16(29),
                                    CreatedAt = reader.IsDBNull(30) ? DateTimeOffset.MinValue : reader.GetDateTime(30),
                                    UpdatedAt = reader.IsDBNull(31) ? DateTimeOffset.MinValue : reader.GetDateTime(31)
                                };
                            }

                            // Next chapter
                            if (!reader.IsDBNull(32))
                            {
                                bookmark.NextChapter = new BookmarkChapter
                                {
                                    Id = reader.GetGuid(32),
                                    Number = reader.GetFloat(33),
                                    Title = reader.IsDBNull(34) ? string.Empty : reader.GetString(34),
                                    Pages = reader.IsDBNull(35) ? (short)0 : reader.GetInt16(35),
                                    CreatedAt = reader.IsDBNull(36) ? DateTimeOffset.MinValue : reader.GetDateTime(36),
                                    UpdatedAt = reader.IsDBNull(37) ? DateTimeOffset.MinValue : reader.GetDateTime(37)
                                };
                            }

                            bookmarks.Add(bookmark);
                        }
                    }
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
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
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
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM user_bookmarks_unread WHERE user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);

                    var result = await cmd.ExecuteScalarAsync();
                    var count = result != null ? (long)result : 0;

                    await _postgresService.CloseAsync();
                    return Ok(SuccessResponse<int>.Create((int)count));
                }
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
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                using (var cmd = new NpgsqlCommand("SELECT batch_update_user_bookmarks(@p_user_id, @p_manga_ids, @p_chapter_numbers)", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_user_id", userId);
                    cmd.Parameters.AddWithValue("p_manga_ids", new Guid[] { mangaId });
                    cmd.Parameters.AddWithValue("p_chapter_numbers", new double[] { request.ChapterNumber });

                    await cmd.ExecuteNonQueryAsync();
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Bookmark updated successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                if (ex.Message.Contains("Chapter not found"))
                {
                    return BadRequest(ErrorResponse.Create("Bad Request", ex.Message));
                }
                return StatusCode(500, ErrorResponse.Create("Failed to update bookmark", ex.Message));
            }
        }

        /// <summary>
        /// Get last read chapter
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>The last read chapter details.</returns>
        [HttpGet("{mangaId}")]
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
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
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                var query = @"
                    SELECT c.id, c.number, c.title, c.pages, c.created_at, c.updated_at
                    FROM user_bookmarks ub
                    INNER JOIN chapters c ON ub.last_read_chapter_id = c.id
                    WHERE ub.user_id = @userId AND ub.manga_id = @mangaId";

                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("mangaId", mangaId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            await _postgresService.CloseAsync();
                            return Ok(SuccessResponse<LastReadResponse>.Create(null!));
                        }

                        var response = new LastReadResponse
                        {
                            Id = reader.GetGuid(0),
                            Number = reader.GetFloat(1),
                            Title = reader.GetString(2),
                            Pages = reader.GetInt16(3),
                            CreatedAt = reader.GetDateTime(4),
                            UpdatedAt = reader.GetDateTime(5)
                        };

                        await _postgresService.CloseAsync();
                        return Ok(SuccessResponse<LastReadResponse>.Create(response));
                    }
                }
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
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                // Check if bookmark exists
                using (var checkCmd = new NpgsqlCommand("SELECT id FROM user_bookmarks WHERE user_id = @userId AND manga_id = @mangaId", _postgresService.Connection))
                {
                    checkCmd.Parameters.AddWithValue("userId", userId);
                    checkCmd.Parameters.AddWithValue("mangaId", mangaId);

                    var result = await checkCmd.ExecuteScalarAsync();
                    if (result == null)
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("Not Found", "Bookmark not found"));
                    }
                }

                // Delete the bookmark
                using (var deleteCmd = new NpgsqlCommand("DELETE FROM user_bookmarks WHERE user_id = @userId AND manga_id = @mangaId", _postgresService.Connection))
                {
                    deleteCmd.Parameters.AddWithValue("userId", userId);
                    deleteCmd.Parameters.AddWithValue("mangaId", mangaId);

                    await deleteCmd.ExecuteNonQueryAsync();
                }

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
                return BadRequest(ErrorResponse.Create("Bad Request", "No items provided"));
            }
            if (request.Items.Count > maxBatchSize)
            {
                return BadRequest(ErrorResponse.Create("Bad Request", $"Batch size exceeds maximum of {maxBatchSize}"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                using (var cmd = new NpgsqlCommand("SELECT batch_update_user_bookmarks(@p_user_id, @p_manga_ids, @p_chapter_numbers)", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_user_id", userId);
                    cmd.Parameters.AddWithValue("p_manga_ids", request.Items.Select(i => i.MangaId).ToArray());
                    cmd.Parameters.AddWithValue("p_chapter_numbers", request.Items.Select(i => i.ChapterNumber).ToArray());

                    await cmd.ExecuteNonQueryAsync();
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Bookmarks updated successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                if (ex.Message.Contains("Chapter not found") || ex.Message.Contains("Array lengths"))
                {
                    return BadRequest(ErrorResponse.Create("Bad Request", ex.Message));
                }
                return StatusCode(500, ErrorResponse.Create("Failed to batch update bookmarks", ex.Message));
            }
        }
    }
}