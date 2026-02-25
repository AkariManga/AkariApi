using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Dapper;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/genre")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class GenreController : ControllerBase
    {
        private readonly PostgresService _postgresService;

        public GenreController(PostgresService postgresService)
        {
            _postgresService = postgresService;
        }

        /// <summary>
        /// Get manga by genre
        /// </summary>
        /// <param name="name">The genre name to search for.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of manga.</returns>
        [HttpGet("{name}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetGenreByName(string name, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            var normalizedName = name.ToLower();

            try
            {
                await _postgresService.OpenAsync();

                var countQuery = "SELECT COUNT(*) FROM manga WHERE lower_text_array(genres) @> ARRAY[@name]";
                long totalCountLong = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    countQuery, new { name = normalizedName });
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = @"
                    SELECT id, title, cover, description, status, type, authors, genres, view_count AS ""Views"", score, mal_id, ani_id, created_at, updated_at, alternative_titles
                    FROM manga
                    WHERE lower_text_array(genres) @> ARRAY[@name]
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";

                var mangaList = (await _postgresService.Connection.QueryAsync<MangaResponse>(
                    selectQuery, new { name = normalizedName, limit = clampedPageSize, offset })).ToList();

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<MangaListResponse>.Create(new MangaListResponse
                {
                    Items = mangaList,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga by genre", ex.Message));
            }
        }
    }
}
