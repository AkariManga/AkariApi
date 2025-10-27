using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using AkariApi.Attributes;
using Supabase.Postgrest.Exceptions;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/lists")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class ListController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;

        public ListController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
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
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.UserId == userId)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);

                var offset = (clampedPage - 1) * clampedPageSize;

                var response = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.UserId == userId)
                    .Select("*")
                    .Order("updated_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var lists = response.Models.Select(l => new UserMangaListResponse
                {
                    Id = l.Id,
                    UserId = l.UserId,
                    Title = l.Title,
                    Description = l.Description,
                    IsPublic = l.IsPublic,
                    CreatedAt = l.CreatedAt,
                    UpdatedAt = l.UpdatedAt
                }).ToList();

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
                await _supabaseService.InitializeAsync();

                var listResponse = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.Id == id)
                    .Select("*")
                    .Get();

                if (!listResponse.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                var list = listResponse.Models.First();

                // Get all entries for this list (RLS will handle access control)
                var entriesResponse = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.ListId == id)
                    .Select("*")
                    .Order("order_index", Supabase.Postgrest.Constants.Ordering.Ascending)
                    .Get();

                var entries = entriesResponse.Models.Select(e => new UserMangaListEntryResponse
                {
                    Id = e.Id,
                    ListId = e.ListId,
                    MangaId = e.MangaId,
                    OrderIndex = e.OrderIndex,
                    CreatedAt = e.CreatedAt,
                    UpdatedAt = e.UpdatedAt
                }).ToList();

                var result = new UserMangaListWithEntriesResponse
                {
                    Id = list.Id,
                    UserId = list.UserId,
                    Title = list.Title,
                    Description = list.Description,
                    IsPublic = list.IsPublic,
                    CreatedAt = list.CreatedAt,
                    UpdatedAt = list.UpdatedAt,
                    Entries = entries
                };

                return Ok(SuccessResponse<UserMangaListWithEntriesResponse>.Create(result));
            }
            catch (Exception ex)
            {
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
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var totalCount = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.UserId == userId)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);

                var offset = (clampedPage - 1) * clampedPageSize;

                var response = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.UserId == userId)
                    .Select("*")
                    .Order("updated_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var lists = response.Models.Select(l => new UserMangaListResponse
                {
                    Id = l.Id,
                    UserId = l.UserId,
                    Title = l.Title,
                    Description = l.Description,
                    IsPublic = l.IsPublic,
                    CreatedAt = l.CreatedAt,
                    UpdatedAt = l.UpdatedAt
                }).ToList();

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
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var dto = new UserMangaListDto
                {
                    UserId = userId,
                    Title = request.Title,
                    Description = request.Description,
                    IsPublic = request.IsPublic,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                var response = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Insert(dto);

                var createdList = response.Models.First();

                var result = new UserMangaListResponse
                {
                    Id = createdList.Id,
                    UserId = createdList.UserId,
                    Title = createdList.Title,
                    Description = createdList.Description,
                    IsPublic = createdList.IsPublic,
                    CreatedAt = createdList.CreatedAt,
                    UpdatedAt = createdList.UpdatedAt
                };

                return CreatedAtAction(nameof(GetListWithEntries), new { id = result.Id }, SuccessResponse<UserMangaListResponse>.Create(result));
            }
            catch (Exception ex)
            {
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
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error));
                }

                var listResp = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.Id == id)
                    .Select("*")
                    .Get();

                if (!listResp.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                // Compute next order index (max + 1)
                var entriesResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.ListId == id)
                    .Select("order_index")
                    .Order("order_index", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(1)
                    .Get();

                var nextIndex = 0;
                if (entriesResp.Models.Any())
                {
                    nextIndex = entriesResp.Models.First().OrderIndex + 1;
                }

                var entryDto = new UserMangaListEntryDto
                {
                    ListId = id,
                    MangaId = request.MangaId,
                    OrderIndex = nextIndex,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                var insertResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Insert(entryDto);

                var created = insertResp.Models.First();

                var result = new UserMangaListEntryResponse
                {
                    Id = created.Id,
                    ListId = created.ListId,
                    MangaId = created.MangaId,
                    OrderIndex = created.OrderIndex,
                    CreatedAt = created.CreatedAt,
                    UpdatedAt = created.UpdatedAt
                };

                return CreatedAtAction(nameof(GetListWithEntries), new { id }, SuccessResponse<UserMangaListEntryResponse>.Create(result));
            }
            catch (PostgrestException ex) when (ex.Message.Contains("duplicate key") || ex.Message.Contains("23505"))
            {
                // Manga already exists in the list, return the existing entry
                var existingResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.ListId == id && e.MangaId == request.MangaId)
                    .Select("*")
                    .Get();

                if (existingResp.Models.Any())
                {
                    var existing = existingResp.Models.First();
                    var result = new UserMangaListEntryResponse
                    {
                        Id = existing.Id,
                        ListId = existing.ListId,
                        MangaId = existing.MangaId,
                        OrderIndex = existing.OrderIndex,
                        CreatedAt = existing.CreatedAt,
                        UpdatedAt = existing.UpdatedAt
                    };

                    return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(result));
                }
                else
                {
                    // Should not happen, but fallback
                    return StatusCode(500, ErrorResponse.Create("Failed to add entry", "Unexpected error occurred"));
                }
            }
            catch (Exception ex)
            {
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

                var listResp = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.Id == id)
                    .Select("*")
                    .Get();

                if (!listResp.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                // Verify entry exists and belongs to this list
                var entryResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.Id == entryId && e.ListId == id)
                    .Select("*")
                    .Get();

                if (!entryResp.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                }

                await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.Id == entryId)
                    .Delete();

                return Ok(SuccessResponse<string>.Create("Entry removed successfully"));
            }
            catch (Exception ex)
            {
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

                // Check if list exists and belongs to user
                var listResp = await _supabaseService.Client
                    .From<UserMangaListDto>()
                    .Where(l => l.Id == id && l.UserId == userId)
                    .Select("*")
                    .Get();

                if (!listResp.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "List not found or access denied"));
                }

                // Check if entry exists and belongs to this list
                var entryResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.Id == entryId && e.ListId == id)
                    .Select("*")
                    .Get();

                if (!entryResp.Models.Any())
                {
                    return NotFound(ErrorResponse.Create("Not found", "Entry not found or does not belong to this list"));
                }

                var entry = entryResp.Models.First();
                var currentOrder = entry.OrderIndex;
                var newOrder = request.NewOrderIndex;

                if (currentOrder == newOrder)
                {
                    // No change needed
                    var noChangeResult = new UserMangaListEntryResponse
                    {
                        Id = entry.Id,
                        ListId = entry.ListId,
                        MangaId = entry.MangaId,
                        OrderIndex = entry.OrderIndex,
                        CreatedAt = entry.CreatedAt,
                        UpdatedAt = entry.UpdatedAt
                    };
                    return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(noChangeResult));
                }

                await _supabaseService.Client.Rpc("move_list_entry", new { p_list_id = id, p_entry_id = entryId, p_new_order_index = newOrder });
                var updatedResp = await _supabaseService.Client
                    .From<UserMangaListEntryDto>()
                    .Where(e => e.Id == entryId)
                    .Select("*")
                    .Get();

                var updatedEntry = updatedResp.Models.First();

                var result = new UserMangaListEntryResponse
                {
                    Id = updatedEntry.Id,
                    ListId = updatedEntry.ListId,
                    MangaId = updatedEntry.MangaId,
                    OrderIndex = updatedEntry.OrderIndex,
                    CreatedAt = updatedEntry.CreatedAt,
                    UpdatedAt = updatedEntry.UpdatedAt
                };

                return Ok(SuccessResponse<UserMangaListEntryResponse>.Create(result));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to update entry order", ex.Message));
            }
        }
    }
}