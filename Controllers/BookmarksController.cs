using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Supabase.Postgrest;
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
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public BookmarksController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
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

                var totalCount = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);

                var response = await _supabaseService.Client.Rpc("get_user_bookmarks", new { p_user_id = userId.ToString(), p_page = clampedPage, p_limit = clampedPageSize });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                    {
                        Items = new List<BookmarkResponse>(),
                        TotalItems = totalCount,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var bookmarks = JsonSerializer.Deserialize<List<BookmarkResponse>>(response.Content, _jsonOptions);

                return Ok(SuccessResponse<BookmarkListResponse>.Create(new BookmarkListResponse
                {
                    Items = bookmarks ?? new List<BookmarkResponse>(),
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
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

                var count = await _supabaseService.Client
                    .From<UserBookmarkUnreadDto>()
                    .Where(b => b.UserId == userId)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);

                return Ok(SuccessResponse<int>.Create(count));
            }
            catch (Exception ex)
            {
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

                await _supabaseService.Client.Rpc("batch_update_user_bookmarks", new {
                    p_user_id = userId,
                    p_manga_ids = new Guid[] { mangaId },
                    p_chapter_numbers = new double[] { request.ChapterNumber }
                });

                return Ok(SuccessResponse<string>.Create("Bookmark updated successfully"));
            }
            catch (Exception ex)
            {
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

                var bookmark = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Single();

                if (bookmark == null || bookmark.LastReadChapterId == null)
                {
                    return Ok(SuccessResponse<LastReadResponse>.Create(null!));
                }

                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.Id == bookmark.LastReadChapterId)
                    .Single();

                if (chapter == null)
                {
                    return Ok(SuccessResponse<LastReadResponse>.Create(null!));
                }

                var response = new LastReadResponse
                {
                    Id = chapter.Id,
                    Number = chapter.Number,
                    Title = chapter.Title,
                    Pages = chapter.Pages,
                    CreatedAt = chapter.CreatedAt,
                    UpdatedAt = chapter.UpdatedAt
                };

                return Ok(SuccessResponse<LastReadResponse>.Create(response));
            }
            catch (Exception ex)
            {
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

                var bookmark = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Single();

                if (bookmark == null)
                {
                    return NotFound(ErrorResponse.Create("Not Found", "Bookmark not found"));
                }

                await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Delete();

                return Ok(SuccessResponse<string>.Create("Bookmark deleted successfully"));
            }
            catch (Exception ex)
            {
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

                await _supabaseService.Client.Rpc("batch_update_user_bookmarks", new {
                    p_user_id = userId,
                    p_manga_ids = request.Items.Select(i => i.MangaId).ToArray(),
                    p_chapter_numbers = request.Items.Select(i => i.ChapterNumber).ToArray()
                });

                return Ok(SuccessResponse<string>.Create("Bookmarks updated successfully"));
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Chapter not found") || ex.Message.Contains("Array lengths"))
                {
                    return BadRequest(ErrorResponse.Create("Bad Request", ex.Message));
                }
                return StatusCode(500, ErrorResponse.Create("Failed to batch update bookmarks", ex.Message));
            }
        }
    }
}