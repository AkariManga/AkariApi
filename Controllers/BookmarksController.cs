using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Supabase.Postgrest;

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
        /// Retrieves the user's bookmarks with pagination.
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A list of the user's bookmarks.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<BookmarkListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetBookmarks([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
                }

                var totalCount = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);

                var response = await _supabaseService.Client.Rpc("get_user_bookmarks", new { p_user_id = userId.ToString(), p_page = clampedPage, p_limit = clampedPageSize });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<BookmarkListResponse>.Success(new BookmarkListResponse
                    {
                        Items = new List<BookmarkResponse>(),
                        TotalItems = totalCount,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var bookmarks = JsonSerializer.Deserialize<List<BookmarkResponse>>(response.Content, _jsonOptions);

                return Ok(ApiResponse<BookmarkListResponse>.Success(new BookmarkListResponse
                {
                    Items = bookmarks ?? new List<BookmarkResponse>(),
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve bookmarks", ex.Message));
            }
        }

        /// <summary>
        /// Updates the last read chapter for a manga in the user's bookmarks.
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <param name="request">The update request containing chapter ID.</param>
        /// <returns>Success message.</returns>
        [HttpPut("{mangaId}")]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> UpdateReadChapter(Guid mangaId, [FromBody] UpdateBookmarkRequest request)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
                }

                var existingBookmark = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Single();

                if (existingBookmark != null)
                {
                    existingBookmark.LastReadChapterId = request.ChapterId;
                    existingBookmark.UpdatedAt = DateTimeOffset.UtcNow;
                    await _supabaseService.Client.From<UserBookmarkDto>().Update(existingBookmark);
                }
                else
                {
                    var newBookmark = new UserBookmarkDto
                    {
                        UserId = userId,
                        MangaId = mangaId,
                        LastReadChapterId = request.ChapterId,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await _supabaseService.Client.From<UserBookmarkDto>().Insert(newBookmark);
                }

                return Ok(ApiResponse<string>.Success("Bookmark updated successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to update bookmark", ex.Message));
            }
        }

        /// <summary>
        /// Gets the last read chapter for a specific manga.
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>The last read chapter details.</returns>
        [HttpGet("{mangaId}")]
        [ProducesResponseType(typeof(ApiResponse<LastReadResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetLastReadChapter(Guid mangaId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
                }

                var bookmark = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Single();

                if (bookmark == null || bookmark.LastReadChapterId == null)
                {
                    return Ok(ApiResponse<LastReadResponse>.Success(null!));
                }

                var chapter = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.Id == bookmark.LastReadChapterId)
                    .Single();

                if (chapter == null)
                {
                    return Ok(ApiResponse<LastReadResponse>.Success(null!));
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

                return Ok(ApiResponse<LastReadResponse>.Success(response));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve last read chapter", ex.Message));
            }
        }

        /// <summary>
        /// Deletes the bookmark for a specific manga.
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>Success message.</returns>
        [HttpDelete("{mangaId}")]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> DeleteBookmark(Guid mangaId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
                }

                var bookmark = await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Single();

                if (bookmark == null)
                {
                    return NotFound(ApiResponse<ErrorData>.Error("Not Found", "Bookmark not found"));
                }

                await _supabaseService.Client
                    .From<UserBookmarkDto>()
                    .Where(b => b.UserId == userId && b.MangaId == mangaId)
                    .Delete();

                return Ok(ApiResponse<string>.Success("Bookmark deleted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to delete bookmark", ex.Message));
            }
        }
    }
}