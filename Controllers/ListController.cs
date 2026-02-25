using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using AkariApi.Attributes;
using Supabase.Postgrest.Exceptions;
using Npgsql;
using Dapper;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/lists")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class ListController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;

        public ListController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        private static UserMangaListEntryResponse MapEntryRow(dynamic r)
        {
            return new UserMangaListEntryResponse
            {
                Id = (Guid)r.id,
                ListId = (Guid)r.list_id,
                MangaId = (Guid)r.manga_id,
                OrderIndex = (int)r.order_index,
                CreatedAt = (DateTime)r.created_at,
                UpdatedAt = (DateTime)r.updated_at,
                MangaTitle = (string)r.manga_title,
                MangaCover = (string)r.manga_cover,
                MangaDescription = (string)r.manga_description
            };
        }

        /// <summary>
        /// Get user lists
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of the user's manga lists.</returns>
        [HttpGet("user/{userId}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListPaginatedResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUserLists(Guid userId, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                Guid? currentUserId = null;
                var (authUserId, authError) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (string.IsNullOrEmpty(authError))
                {
                    currentUserId = authUserId;
                }

                string whereClause = currentUserId == userId ? "WHERE user_id = @userId" : "WHERE user_id = @userId AND is_public = true";

                var countQuery = $"SELECT COUNT(*) FROM user_manga_lists {whereClause}";
                long totalCountLong = await _postgresService.Connection.ExecuteScalarAsync<long>(countQuery, new { userId });
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = $@"
                    SELECT id, user_id, title, description, is_public, created_at, updated_at, (SELECT COUNT(*) FROM user_manga_list_entries WHERE list_id = user_manga_lists.id) AS total_entries
                    FROM user_manga_lists
                    {whereClause}
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";

                var lists = (await _postgresService.Connection.QueryAsync<UserMangaListResponse>(selectQuery, new { userId, limit = clampedPageSize, offset })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<UserMangaListPaginatedResponse>.Create(new UserMangaListPaginatedResponse
                {
                    Items = lists,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user lists", ex.Message));
            }
        }

        /// <summary>
        /// Get list with entries
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>The manga list with all its entries.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListWithEntriesResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetListWithEntries(Guid id)
        {
            try
            {
                await _postgresService.OpenAsync();

                Guid? currentUserId = null;
                var (authUserId, authError) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (string.IsNullOrEmpty(authError))
                {
                    currentUserId = authUserId;
                }

                var listQuery = "SELECT l.id, l.user_id, l.title, l.description, l.is_public, l.created_at, l.updated_at, u.username, u.display_name, u.role, u.banned FROM user_manga_lists l JOIN profiles u ON l.user_id = u.id WHERE l.id = @id";
                var listRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(listQuery, new { id });

                UserMangaListWithEntriesResponse? result = null;
                if (listRow != null)
                {
                    var listUserId = (Guid)listRow.user_id;
                    var isPublic = (bool)listRow.is_public;
                    if (isPublic || currentUserId == listUserId)
                    {
                        result = new UserMangaListWithEntriesResponse
                        {
                            Id = (Guid)listRow.id,
                            UserId = listUserId,
                            Title = (string)listRow.title,
                            Description = listRow.description == null ? null : (string)listRow.description,
                            IsPublic = isPublic,
                            CreatedAt = (DateTime)listRow.created_at,
                            UpdatedAt = (DateTime)listRow.updated_at,
                            User = new UserResponse
                            {
                                UserId = listUserId,
                                Username = (string)listRow.username,
                                DisplayName = (string)listRow.display_name,
                                Role = (UserRole)Enum.Parse(typeof(UserRole), (string)listRow.role),
                                Banned = (bool)listRow.banned
                            },
                            Entries = new List<UserMangaListEntryResponse>()
                        };
                    }
                }

                if (result == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                var entriesQuery = "SELECT e.id, e.list_id, e.manga_id, e.order_index, e.created_at, e.updated_at, m.title AS manga_title, m.cover AS manga_cover, m.description AS manga_description FROM user_manga_list_entries e JOIN manga m ON e.manga_id = m.id WHERE e.list_id = @listId ORDER BY e.order_index ASC";
                var entries = (await _postgresService.Connection.QueryAsync<UserMangaListEntryResponse>(entriesQuery, new { listId = id })).ToList();

                result.Entries = entries;
                result.TotalEntries = entries.Count;

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<UserMangaListWithEntriesResponse>.Create(result));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve list", ex.Message));
            }
        }

        /// <summary>
        /// Get my lists
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of the user's manga lists.</returns>
        [HttpGet("user/me")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListPaginatedResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMyLists([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                long totalCountLong = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM user_manga_lists WHERE user_id = @userId",
                    new { userId });
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = @"
                    SELECT id, user_id, title, description, is_public, created_at, updated_at, (SELECT COUNT(*) FROM user_manga_list_entries WHERE list_id = user_manga_lists.id) AS total_entries
                    FROM user_manga_lists
                    WHERE user_id = @userId
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";

                var lists = (await _postgresService.Connection.QueryAsync<UserMangaListResponse>(selectQuery, new { userId, limit = clampedPageSize, offset })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<UserMangaListPaginatedResponse>.Create(new UserMangaListPaginatedResponse
                {
                    Items = lists,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user lists", ex.Message));
            }
        }

        /// <summary>
        /// Create a list
        /// </summary>
        /// <param name="request">The create list request.</param>
        /// <returns>The created manga list.</returns>
        [HttpPost]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListResponse>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> CreateList([FromBody] CreateUserMangaListRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var insertQuery = "INSERT INTO user_manga_lists (user_id, title, description, is_public, created_at, updated_at) VALUES (@userId, @title, @description, @isPublic, @createdAt, @updatedAt) RETURNING id, user_id, title, description, is_public, created_at, updated_at";
                var now = DateTimeOffset.UtcNow;
                var result = await _postgresService.Connection.QueryFirstOrDefaultAsync<UserMangaListResponse>(insertQuery, new
                {
                    userId,
                    title = request.Title,
                    description = request.Description ?? (object)DBNull.Value,
                    isPublic = request.IsPublic,
                    createdAt = now,
                    updatedAt = now
                });

                if (result == null)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to create list"));
                }

                result.TotalEntries = 0;

                await _postgresService.CloseAsync();

                return CreatedAtAction(nameof(GetListWithEntries), new { id = result.Id }, SuccessResponse<UserMangaListResponse>.Create(result));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to create list", ex.Message));
            }
        }

        /// <summary>
        /// Add entry to list
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="request">The create entry request (manga id).</param>
        /// <returns>The created list entry.</returns>
        [HttpPost("{id}")]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListEntryResponse>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> AddEntryToList(Guid id, [FromBody] CreateUserMangaListEntryRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var listOwner = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT user_id FROM user_manga_lists WHERE id = @id",
                    new { id });

                if (listOwner == null || listOwner != userId)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                var nextIndex = Convert.ToInt32(await _postgresService.Connection.ExecuteScalarAsync<object>(
                    "SELECT COALESCE(MAX(order_index), -1) FROM user_manga_list_entries WHERE list_id = @listId",
                    new { listId = id })) + 1;

                try
                {
                    var insertQuery = "INSERT INTO user_manga_list_entries (list_id, manga_id, order_index, created_at, updated_at) VALUES (@listId, @mangaId, @orderIndex, @createdAt, @updatedAt) RETURNING id";
                    var now = DateTimeOffset.UtcNow;
                    var entryId = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(insertQuery, new
                    {
                        listId = id,
                        mangaId = request.MangaId,
                        orderIndex = nextIndex,
                        createdAt = now,
                        updatedAt = now
                    });

                    if (entryId == null || entryId == Guid.Empty)
                    {
                        await _postgresService.CloseAsync();
                        return StatusCode(500, ErrorResponse.Create("Failed to add entry", "Could not retrieve inserted entry ID"));
                    }

                    var selectQuery = "SELECT e.id, e.list_id, e.manga_id, e.order_index, e.created_at, e.updated_at, m.title AS manga_title, m.cover AS manga_cover, m.description AS manga_description FROM user_manga_list_entries e JOIN manga m ON e.manga_id = m.id WHERE e.id = @entryId";
                    var entryRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(selectQuery, new { entryId = entryId.Value });
                    if (entryRow == null)
                    {
                        await _postgresService.CloseAsync();
                        return StatusCode(500, ErrorResponse.Create("Failed to add entry", "Could not retrieve inserted entry"));
                    }

                    var resultEntry = MapEntryRow(entryRow);
                    await _postgresService.CloseAsync();

                    return CreatedAtAction(nameof(GetListWithEntries), new { id }, SuccessResponse<UserMangaListEntryResponse>.Create(resultEntry));
                }
                catch (PostgresException ex) when (ex.SqlState == "23505") // unique violation
                {
                    var existingQuery = "SELECT e.id, e.list_id, e.manga_id, e.order_index, e.created_at, e.updated_at, m.title AS manga_title, m.cover AS manga_cover, m.description AS manga_description FROM user_manga_list_entries e JOIN manga m ON e.manga_id = m.id WHERE e.list_id = @listId AND e.manga_id = @mangaId";
                    var existingRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(existingQuery, new { listId = id, mangaId = request.MangaId });
                    if (existingRow != null)
                    {
                        var existing = MapEntryRow(existingRow);
                        await _postgresService.CloseAsync();
                        return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(existing));
                    }

                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to add entry", "Unexpected error occurred"));
                }
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to add entry", ex.Message));
            }
        }

        /// <summary>
        /// Remove entry from list
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="entryId">The entry ID.</param>
        /// <returns>Success message on deletion.</returns>
        [HttpDelete("{id}/{entryId}")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> RemoveEntryFromList(Guid id, Guid entryId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                await _postgresService.OpenAsync();

                var listExists = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId",
                    new { id, userId });

                if (listExists == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                var entryExists = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM user_manga_list_entries WHERE id = @entryId AND list_id = @listId",
                    new { entryId, listId = id });

                if (entryExists == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                }

                await _postgresService.Connection.ExecuteAsync(
                    "DELETE FROM user_manga_list_entries WHERE id = @entryId",
                    new { entryId });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Entry removed successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to remove entry", ex.Message));
            }
        }

        /// <summary>
        /// Update entry order
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="entryId">The entry ID.</param>
        /// <param name="request">The update request with the new order index.</param>
        /// <returns>The updated entry.</returns>
        [HttpPut("{id}/{entryId}")]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListEntryResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateEntryOrder(Guid id, Guid entryId, [FromBody] UpdateUserMangaListEntryRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                await _postgresService.OpenAsync();

                var listExists = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId",
                    new { id, userId });

                if (listExists == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                var entryRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(
                    "SELECT e.id, e.list_id, e.manga_id, e.order_index, e.created_at, e.updated_at, m.title AS manga_title, m.cover AS manga_cover, m.description AS manga_description FROM user_manga_list_entries e JOIN manga m ON e.manga_id = m.id WHERE e.id = @entryId AND e.list_id = @listId",
                    new { entryId, listId = id });

                if (entryRow == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                }

                int currentOrder = (int)entryRow.order_index;

                if (currentOrder == request.NewOrderIndex)
                {
                    var noChangeResult = MapEntryRow(entryRow);
                    await _postgresService.CloseAsync();
                    return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(noChangeResult));
                }

                if (request.NewOrderIndex > currentOrder)
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "UPDATE user_manga_list_entries SET order_index = order_index - 1 WHERE list_id = @listId AND order_index > @currentOrder AND order_index <= @newOrder",
                        new { listId = id, currentOrder, newOrder = request.NewOrderIndex });
                }
                else
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "UPDATE user_manga_list_entries SET order_index = order_index + 1 WHERE list_id = @listId AND order_index >= @newOrder AND order_index < @currentOrder",
                        new { listId = id, newOrder = request.NewOrderIndex, currentOrder });
                }

                await _postgresService.Connection.ExecuteAsync(
                    "UPDATE user_manga_list_entries SET order_index = @newOrder, updated_at = NOW() WHERE id = @entryId",
                    new { newOrder = request.NewOrderIndex, entryId });

                var updatedRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(
                    "SELECT e.id, e.list_id, e.manga_id, e.order_index, e.created_at, e.updated_at, m.title AS manga_title, m.cover AS manga_cover, m.description AS manga_description FROM user_manga_list_entries e JOIN manga m ON e.manga_id = m.id WHERE e.id = @entryId",
                    new { entryId });

                if (updatedRow == null)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to retrieve updated entry"));
                }

                var result = MapEntryRow(updatedRow);
                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(result));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to update entry order", ex.Message));
            }
        }

        /// <summary>
        /// Delete a list
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message on deletion.</returns>
        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> DeleteList(Guid id)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                await _postgresService.OpenAsync();

                var listExists = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId",
                    new { id, userId });

                if (listExists == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                await _postgresService.Connection.ExecuteAsync(
                    "DELETE FROM user_manga_lists WHERE id = @id",
                    new { id });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("List deleted successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to delete list", ex.Message));
            }
        }

        /// <summary>
        /// Get my lists containing a specific manga
        /// </summary>
        /// <param name="mangaId">The manga ID.</param>
        /// <returns>An array of the user's list IDs that contain the specified manga.</returns>
        [HttpGet("user/me/manga/{mangaId}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<List<Guid>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMyListsContainingManga(Guid mangaId)
        {
            try
            {
                await _postgresService.OpenAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var selectQuery = @"
                    SELECT l.id
                    FROM user_manga_lists l
                    INNER JOIN user_manga_list_entries e ON l.id = e.list_id
                    WHERE l.user_id = @userId AND e.manga_id = @mangaId
                    ORDER BY l.updated_at DESC";

                var listIds = (await _postgresService.Connection.QueryAsync<Guid>(selectQuery, new { userId, mangaId })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<List<Guid>>.Create(listIds));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve lists containing manga", ex.Message));
            }
        }
    }
}
