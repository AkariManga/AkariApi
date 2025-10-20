using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/author")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class AuthorController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;

        public AuthorController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        /// <summary>
        /// Retrieves a list of manga where the authors include the specified name.
        /// </summary>
        /// <param name="name">The author name to search for.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of manga.</returns>
        [HttpGet("{name}")]
        [CacheControl(3600, 600)]
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetAuthorByName(string name, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<MangaDto>()
                    .Filter("authors", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { name })
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (clampedPage - 1) * clampedPageSize;

                var response = await _supabaseService.Client
                    .From<MangaDto>()
                    .Select("*")
                    .Filter("authors", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { name })
                    .Order("updated_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var mangaList = response.Models.Select(m => new MangaResponse
                {
                    Id = m.Id,
                    Title = m.Title,
                    Cover = m.Cover,
                    Description = m.Description,
                    Status = m.Status,
                    Type = m.Type,
                    Authors = m.Authors,
                    Genres = m.Genres,
                    AlternativeTitles = m.AlternativeTitles,
                    MalId = m.MalId,
                    AniId = m.AniId,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt,
                }).ToList();

                return Ok(ApiResponse<MangaListResponse>.Success(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve latest manga", ex.Message));
            }
        }
    }
}