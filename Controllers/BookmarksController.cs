using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Supabase.Postgrest;
using System.Linq;
using Npgsql;

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
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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

                // Get total count
                long totalCount = 0;
                using (var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM user_bookmarks WHERE user_id = @userId", _postgresService.Connection))
                {
                    countCmd.Parameters.AddWithValue("userId", userId);
                    var result = await countCmd.ExecuteScalarAsync();
                    totalCount = result != null ? (long)result : 0;
                }

                // Get bookmarks using RPC
                var rpcQuery = "SELECT get_user_bookmarks(@p_user_id, @p_page, @p_limit)";
                using (var cmd = new NpgsqlCommand(rpcQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_user_id", userId);
                    cmd.Parameters.AddWithValue("p_page", clampedPage);
                    cmd.Parameters.AddWithValue("p_limit", clampedPageSize);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            await _postgresService.CloseAsync();
                            return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                            {
                                Items = new List<BookmarkResponse>(),
                                TotalItems = (int)totalCount,
                                CurrentPage = clampedPage,
                                PageSize = clampedPageSize
                            }));
                        }

                        var content = reader.GetString(0);
                        if (string.IsNullOrEmpty(content))
                        {
                            await _postgresService.CloseAsync();
                            return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                            {
                                Items = new List<BookmarkResponse>(),
                                TotalItems = (int)totalCount,
                                CurrentPage = clampedPage,
                                PageSize = clampedPageSize
                            }));
                        }

                        var bookmarks = JsonSerializer.Deserialize<List<BookmarkResponse>>(content, _jsonOptions);

                        await _postgresService.CloseAsync();
                        return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                        {
                            Items = bookmarks ?? new List<BookmarkResponse>(),
                            TotalItems = (int)totalCount,
                            CurrentPage = clampedPage,
                            PageSize = clampedPageSize
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve bookmarks", ex.Message));
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

                // Get bookmark
                Guid? lastReadChapterId = null;
                using (var cmd = new NpgsqlCommand("SELECT last_read_chapter_id FROM user_bookmarks WHERE user_id = @userId AND manga_id = @mangaId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("mangaId", mangaId);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null && result != DBNull.Value)
                    {
                        lastReadChapterId = (Guid)result;
                    }
                }

                if (lastReadChapterId == null)
                {
                    await _postgresService.CloseAsync();
                    return Ok(SuccessResponse<LastReadResponse>.Create(null!));
                }

                // Get chapter details
                using (var cmd = new NpgsqlCommand("SELECT id, number, title, pages, created_at, updated_at FROM chapters WHERE id = @chapterId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("chapterId", lastReadChapterId);

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