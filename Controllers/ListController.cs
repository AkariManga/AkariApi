using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using AkariApi.Attributes;
using Supabase.Postgrest.Exceptions;
using Npgsql;

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

        /// <summary>
        /// Get user lists
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of the user's manga lists.</returns>
        [HttpGet("user/{userId}")]
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListPaginatedResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        [OptionalTokenRefresh]
        public async Task<IActionResult> GetUserLists(Guid userId, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                // Try to authenticate, but it's optional
                Guid? currentUserId = null;
                var (authUserId, authError) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (string.IsNullOrEmpty(authError))
                {
                    currentUserId = authUserId;
                }

                // If authenticated and requesting own lists, show all, else only public
                string whereClause = currentUserId == userId ? "WHERE user_id = @userId" : "WHERE user_id = @userId AND is_public = true";

                // Get total count
                var countQuery = $"SELECT COUNT(*) FROM user_manga_lists {whereClause}";
                long totalCountLong;
                using (var countCmd = new NpgsqlCommand(countQuery, _postgresService.Connection))
                {
                    countCmd.Parameters.AddWithValue("userId", userId);
                    var result = await countCmd.ExecuteScalarAsync();
                    totalCountLong = result != null ? (long)result : 0;
                }
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                // Get paginated results
                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = $@"
                    SELECT id, user_id, title, description, is_public, created_at, updated_at, (SELECT COUNT(*) FROM user_manga_list_entries WHERE list_id = user_manga_lists.id) AS total_entries
                    FROM user_manga_lists
                    {whereClause}
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";
                var lists = new List<UserMangaListResponse>();
                using (var selectCmd = new NpgsqlCommand(selectQuery, _postgresService.Connection))
                {
                    selectCmd.Parameters.AddWithValue("userId", userId);
                    selectCmd.Parameters.AddWithValue("limit", clampedPageSize);
                    selectCmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await selectCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var list = new UserMangaListResponse
                            {
                                Id = reader.GetGuid(0),
                                UserId = reader.GetGuid(1),
                                Title = reader.GetString(2),
                                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                                IsPublic = reader.GetBoolean(4),
                                CreatedAt = reader.GetDateTime(5),
                                UpdatedAt = reader.GetDateTime(6),
                                TotalEntries = reader.GetInt32(7)
                            };
                            lists.Add(list);
                        }
                    }
                }

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
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListWithEntriesResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        [OptionalTokenRefresh]
        public async Task<IActionResult> GetListWithEntries(Guid id)
        {
            try
            {
                await _postgresService.OpenAsync();

                // Try to authenticate
                Guid? currentUserId = null;
                var (authUserId, authError) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (string.IsNullOrEmpty(authError))
                {
                    currentUserId = authUserId;
                }

                // Get the list, check access
                var listQuery = "SELECT id, user_id, title, description, is_public, created_at, updated_at FROM user_manga_lists WHERE id = @id";
                UserMangaListWithEntriesResponse? result = null;
                using (var listCmd = new NpgsqlCommand(listQuery, _postgresService.Connection))
                {
                    listCmd.Parameters.AddWithValue("id", id);
                    using (var reader = await listCmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var listUserId = reader.GetGuid(1);
                            var isPublic = reader.GetBoolean(4);
                            if (isPublic || currentUserId == listUserId)
                            {
                                result = new UserMangaListWithEntriesResponse
                                {
                                    Id = reader.GetGuid(0),
                                    UserId = listUserId,
                                    Title = reader.GetString(2),
                                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                                    IsPublic = isPublic,
                                    CreatedAt = reader.GetDateTime(5),
                                    UpdatedAt = reader.GetDateTime(6),
                                    Entries = new List<UserMangaListEntryResponse>()
                                };
                            }
                        }
                    }
                }

                if (result == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                // Get all entries for this list
                var entriesQuery = "SELECT id, list_id, manga_id, order_index, created_at, updated_at FROM user_manga_list_entries WHERE list_id = @listId ORDER BY order_index ASC";
                var entries = new List<UserMangaListEntryResponse>();
                using (var entriesCmd = new NpgsqlCommand(entriesQuery, _postgresService.Connection))
                {
                    entriesCmd.Parameters.AddWithValue("listId", id);
                    using (var reader = await entriesCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var entry = new UserMangaListEntryResponse
                            {
                                Id = reader.GetGuid(0),
                                ListId = reader.GetGuid(1),
                                MangaId = reader.GetGuid(2),
                                OrderIndex = reader.GetInt32(3),
                                CreatedAt = reader.GetDateTime(4),
                                UpdatedAt = reader.GetDateTime(5)
                            };
                            entries.Add(entry);
                        }
                    }
                }

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
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserMangaListPaginatedResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        [RequireTokenRefresh]
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

                // Get total count
                var countQuery = "SELECT COUNT(*) FROM user_manga_lists WHERE user_id = @userId";
                long totalCountLong;
                using (var countCmd = new NpgsqlCommand(countQuery, _postgresService.Connection))
                {
                    countCmd.Parameters.AddWithValue("userId", userId);
                    var result = await countCmd.ExecuteScalarAsync();
                    totalCountLong = result != null ? (long)result : 0;
                }
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                // Get paginated results
                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = @"
                    SELECT id, user_id, title, description, is_public, created_at, updated_at, (SELECT COUNT(*) FROM user_manga_list_entries WHERE list_id = user_manga_lists.id) AS total_entries
                    FROM user_manga_lists
                    WHERE user_id = @userId
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";
                var lists = new List<UserMangaListResponse>();
                using (var selectCmd = new NpgsqlCommand(selectQuery, _postgresService.Connection))
                {
                    selectCmd.Parameters.AddWithValue("userId", userId);
                    selectCmd.Parameters.AddWithValue("limit", clampedPageSize);
                    selectCmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await selectCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var list = new UserMangaListResponse
                            {
                                Id = reader.GetGuid(0),
                                UserId = reader.GetGuid(1),
                                Title = reader.GetString(2),
                                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                                IsPublic = reader.GetBoolean(4),
                                CreatedAt = reader.GetDateTime(5),
                                UpdatedAt = reader.GetDateTime(6),
                                TotalEntries = reader.GetInt32(7)
                            };
                            lists.Add(list);
                        }
                    }
                }

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
        [RequireTokenRefresh]
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
                UserMangaListResponse result;
                using (var cmd = new NpgsqlCommand(insertQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("title", request.Title);
                    cmd.Parameters.AddWithValue("description", request.Description ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("isPublic", request.IsPublic);
                    var now = DateTimeOffset.UtcNow;
                    cmd.Parameters.AddWithValue("createdAt", now);
                    cmd.Parameters.AddWithValue("updatedAt", now);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        await reader.ReadAsync();
                        result = new UserMangaListResponse
                        {
                            Id = reader.GetGuid(0),
                            UserId = reader.GetGuid(1),
                            Title = reader.GetString(2),
                            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                            IsPublic = reader.GetBoolean(4),
                            CreatedAt = reader.GetDateTime(5),
                            UpdatedAt = reader.GetDateTime(6),
                            TotalEntries = 0
                        };
                    }
                }

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
        [RequireTokenRefresh]
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

                // Check if list exists and belongs to user
                var listQuery = "SELECT user_id FROM user_manga_lists WHERE id = @id";
                Guid? listOwner = null;
                using (var listCmd = new NpgsqlCommand(listQuery, _postgresService.Connection))
                {
                    listCmd.Parameters.AddWithValue("id", id);
                    var result = await listCmd.ExecuteScalarAsync();
                    if (result != null)
                    {
                        listOwner = (Guid)result;
                    }
                }

                if (listOwner == null || listOwner != userId)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                // Compute next order index (max + 1)
                var maxIndexQuery = "SELECT COALESCE(MAX(order_index), -1) FROM user_manga_list_entries WHERE list_id = @listId";
                int nextIndex;
                using (var maxCmd = new NpgsqlCommand(maxIndexQuery, _postgresService.Connection))
                {
                    maxCmd.Parameters.AddWithValue("listId", id);
                    var result = await maxCmd.ExecuteScalarAsync();
                    nextIndex = result != null ? (int)(long)result + 1 : 0;
                }

                // Insert entry
                var insertQuery = "INSERT INTO user_manga_list_entries (list_id, manga_id, order_index, created_at, updated_at) VALUES (@listId, @mangaId, @orderIndex, @createdAt, @updatedAt) RETURNING id, list_id, manga_id, order_index, created_at, updated_at";
                UserMangaListEntryResponse resultEntry;
                try
                {
                    using (var insertCmd = new NpgsqlCommand(insertQuery, _postgresService.Connection))
                    {
                        insertCmd.Parameters.AddWithValue("listId", id);
                        insertCmd.Parameters.AddWithValue("mangaId", request.MangaId);
                        insertCmd.Parameters.AddWithValue("orderIndex", nextIndex);
                        var now = DateTimeOffset.UtcNow;
                        insertCmd.Parameters.AddWithValue("createdAt", now);
                        insertCmd.Parameters.AddWithValue("updatedAt", now);
                        using (var reader = await insertCmd.ExecuteReaderAsync())
                        {
                            await reader.ReadAsync();
                            resultEntry = new UserMangaListEntryResponse
                            {
                                Id = reader.GetGuid(0),
                                ListId = reader.GetGuid(1),
                                MangaId = reader.GetGuid(2),
                                OrderIndex = reader.GetInt32(3),
                                CreatedAt = reader.GetDateTime(4),
                                UpdatedAt = reader.GetDateTime(5)
                            };
                        }
                    }

                    await _postgresService.CloseAsync();

                    return CreatedAtAction(nameof(GetListWithEntries), new { id }, SuccessResponse<UserMangaListEntryResponse>.Create(resultEntry));
                }
                catch (PostgresException ex) when (ex.SqlState == "23505") // unique violation
                {
                    // Manga already exists in the list, return the existing entry
                    var existingQuery = "SELECT id, list_id, manga_id, order_index, created_at, updated_at FROM user_manga_list_entries WHERE list_id = @listId AND manga_id = @mangaId";
                    using (var existingCmd = new NpgsqlCommand(existingQuery, _postgresService.Connection))
                    {
                        existingCmd.Parameters.AddWithValue("listId", id);
                        existingCmd.Parameters.AddWithValue("mangaId", request.MangaId);
                        using (var reader = await existingCmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var existing = new UserMangaListEntryResponse
                                {
                                    Id = reader.GetGuid(0),
                                    ListId = reader.GetGuid(1),
                                    MangaId = reader.GetGuid(2),
                                    OrderIndex = reader.GetInt32(3),
                                    CreatedAt = reader.GetDateTime(4),
                                    UpdatedAt = reader.GetDateTime(5)
                                };

                                await _postgresService.CloseAsync();

                                return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(existing));
                            }
                        }
                    }

                    // Should not happen
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
        [RequireTokenRefresh]
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

                // Check if list exists and user owns it
                using (var cmd = new NpgsqlCommand("SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("userId", userId);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                    }
                }

                // Check if entry exists and belongs to this list
                using (var cmd = new NpgsqlCommand("SELECT id FROM user_manga_list_entries WHERE id = @entryId AND list_id = @listId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("entryId", entryId);
                    cmd.Parameters.AddWithValue("listId", id);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                    }
                }

                // Delete the entry
                using (var cmd = new NpgsqlCommand("DELETE FROM user_manga_list_entries WHERE id = @entryId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("entryId", entryId);

                    await cmd.ExecuteNonQueryAsync();
                }

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
        [RequireTokenRefresh]
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

                // Check if list exists and user owns it
                using (var cmd = new NpgsqlCommand("SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("userId", userId);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                    }
                }

                // Check if entry exists and belongs to this list
                using (var cmd = new NpgsqlCommand("SELECT id, list_id, manga_id, order_index, created_at, updated_at FROM user_manga_list_entries WHERE id = @entryId AND list_id = @listId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("entryId", entryId);
                    cmd.Parameters.AddWithValue("listId", id);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            await _postgresService.CloseAsync();
                            return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                        }

                        var currentOrder = reader.GetInt32(3);
                        var newOrder = request.NewOrderIndex;

                        if (currentOrder == newOrder)
                        {
                            // No change needed
                            var noChangeResult = new UserMangaListEntryResponse
                            {
                                Id = reader.GetGuid(0),
                                ListId = reader.GetGuid(1),
                                MangaId = reader.GetGuid(2),
                                OrderIndex = reader.GetInt32(3),
                                CreatedAt = reader.GetDateTime(4),
                                UpdatedAt = reader.GetDateTime(5)
                            };
                            await _postgresService.CloseAsync();
                            return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(noChangeResult));
                        }
                    }
                }

                // Call the RPC function to move the entry
                using (var cmd = new NpgsqlCommand("SELECT move_list_entry(@p_list_id, @p_entry_id, @p_new_order_index)", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("p_list_id", id);
                    cmd.Parameters.AddWithValue("p_entry_id", entryId);
                    cmd.Parameters.AddWithValue("p_new_order_index", request.NewOrderIndex);

                    await cmd.ExecuteNonQueryAsync();
                }

                // Fetch the updated entry
                using (var cmd = new NpgsqlCommand("SELECT id, list_id, manga_id, order_index, created_at, updated_at FROM user_manga_list_entries WHERE id = @entryId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("entryId", entryId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        await reader.ReadAsync();

                        var result = new UserMangaListEntryResponse
                        {
                            Id = reader.GetGuid(0),
                            ListId = reader.GetGuid(1),
                            MangaId = reader.GetGuid(2),
                            OrderIndex = reader.GetInt32(3),
                            CreatedAt = reader.GetDateTime(4),
                            UpdatedAt = reader.GetDateTime(5)
                        };

                        await _postgresService.CloseAsync();
                        return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(result));
                    }
                }
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
        [RequireTokenRefresh]
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

                // Check if list exists and user owns it
                using (var cmd = new NpgsqlCommand("SELECT id FROM user_manga_lists WHERE id = @id AND user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("userId", userId);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                    }
                }

                using (var cmd = new NpgsqlCommand("DELETE FROM user_manga_lists WHERE id = @id", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);

                    await cmd.ExecuteNonQueryAsync();
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("List deleted successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to delete list", ex.Message));
            }
        }
    }
}