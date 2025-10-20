using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;

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
            if (pageSize > 100)
                pageSize = 100;
            if (pageSize < 1)
                pageSize = 20;
            if (page < 1)
                page = 1;

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<MangaDto>()
                    .Filter("authors", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { name })
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (page - 1) * pageSize;

                var response = await _supabaseService.Client
                    .From<MangaDto>()
                    .Select("*")
                    .Filter("authors", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { name })
                    .Order("updated_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Range(offset, offset + pageSize - 1)
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
                    CurrentPage = page,
                    PageSize = pageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve latest manga", ex.Message));
            }
        }
    }
}