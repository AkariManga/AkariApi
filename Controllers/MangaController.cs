using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/manga")]
    [ApiVersion("2.0")]
    public class MangaController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public MangaController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        [HttpGet("list/latest")]
        [CacheControl(600, 300)]
        [ProducesResponseType(typeof(ApiResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetLatestManga([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            if (pageSize > 100)
            {
                pageSize = 100;
            }
            if (pageSize < 1)
            {
                pageSize = 20;
            }
            if (page < 1)
            {
                page = 1;
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<MangaDto>()
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (page - 1) * pageSize;

                var response = await _supabaseService.Client
                    .From<MangaDto>()
                    .Select("*")
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

        [HttpGet("{id}")]
        [CacheControl(3600, 600)]
        [ProducesResponseType(typeof(ApiResponse<MangaDetailResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetMangaById(Guid id)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var manga = await _supabaseService.Client
                    .From<MangaDto>()
                    .Where(m => m.Id == id)
                    .Single();

                if (manga == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Manga not found", status: 404));

                var chaptersResponse = await _supabaseService.Client
                    .From<ChapterDto>()
                    .Where(c => c.MangaId == id)
                    .Order("number", Supabase.Postgrest.Constants.Ordering.Ascending)
                    .Get();

                var response = new MangaDetailResponse
                {
                    Id = manga.Id,
                    Title = manga.Title,
                    Cover = manga.Cover,
                    Description = manga.Description,
                    Status = manga.Status,
                    Type = manga.Type,
                    Authors = manga.Authors,
                    Genres = manga.Genres,
                    AlternativeTitles = manga.AlternativeTitles,
                    MalId = manga.MalId,
                    AniId = manga.AniId,
                    CreatedAt = manga.CreatedAt,
                    UpdatedAt = manga.UpdatedAt,
                    Chapters = chaptersResponse.Models.Select(c => new MangaChapter
                    {
                        Id = c.Id,
                        Title = c.Title,
                        Number = c.Number,
                        Pages = c.Pages
                    }).ToList()
                };

                return Ok(ApiResponse<MangaDetailResponse>.Success(response));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve manga", ex.Message));
            }
        }

        [HttpGet("{id}/{subId}")]
        [CacheControl(86400, 604800)]
        [ProducesResponseType(typeof(ApiResponse<ChapterResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetChapter(Guid id, float subId)
        {
            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client.Rpc("get_chapter_by_manga_and_number", new { _manga_id = id.ToString(), _number = subId });

                if (string.IsNullOrEmpty(response.Content))
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                var chapter = JsonSerializer.Deserialize<ChapterResponse>(response.Content, _jsonOptions);

                if (chapter == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Chapter not found", status: 404));

                return Ok(ApiResponse<ChapterResponse>.Success(chapter));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve chapter", ex.Message));
            }
        }

        [HttpGet("search")]
        [CacheControl(300, 60)]
        [ProducesResponseType(typeof(ApiResponse<List<MangaSearchResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> SearchManga([FromQuery] string query, [FromQuery] int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(ApiResponse<ErrorData>.Error("Search query is required"));

            if (limit > 100)
                limit = 100;
            if (limit < 1)
                limit = 1;

            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client.Rpc("search_manga", new { search_text = query, result_limit = limit });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<List<MangaSearchResponse>>.Success(new List<MangaSearchResponse>()));
                }

                var searchResults = JsonSerializer.Deserialize<List<MangaSearchResponse>>(response.Content, _jsonOptions);

                return Ok(ApiResponse<List<MangaSearchResponse>>.Success(searchResults ?? new List<MangaSearchResponse>()));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to search manga", ex.Message));
            }
        }
    }
}