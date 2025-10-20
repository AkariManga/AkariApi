using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/genre")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class GenreController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;

        public GenreController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        /// <summary>
        /// Retrieves a list of manga where the genres include the specified name.
        /// </summary>
        /// <param name="name">The genre name to search for.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of manga.</returns>
        [HttpGet("{name}")]
        [CacheControl(3600, 600)]
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetGenreByName(string name, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            if (pageSize > 100)
                pageSize = 100;
            if (pageSize < 1)
                pageSize = 20;
            if (page < 1)
                page = 1;

            // Normalize the genre name to title case
            var textInfo = CultureInfo.CurrentCulture.TextInfo;
            var normalizedName = textInfo.ToTitleCase(name.ToLower());

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<MangaDto>()
                    .Filter("genres", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { normalizedName })
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (page - 1) * pageSize;

                var response = await _supabaseService.Client
                    .From<MangaDto>()
                    .Select("*")
                    .Filter("genres", Supabase.Postgrest.Constants.Operator.Contains, new List<string> { normalizedName })
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
