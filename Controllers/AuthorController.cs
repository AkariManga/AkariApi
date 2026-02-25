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
    [Route("v2/author")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class AuthorController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;

        public AuthorController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        /// <summary>
        /// Get manga by author
        /// </summary>
        /// <param name="name">The author name to search for.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of manga.</returns>
        [HttpGet("{name}")]
        [CacheControl(CacheDuration.OneHour, CacheDuration.TwelveHours)]
        [ProducesResponseType(typeof(SuccessResponse<MangaListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetAuthorByName(string name, [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                long totalCountLong = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM manga WHERE @name = ANY(authors)", new { name });
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = @"
                    SELECT id, title, cover, description, status, type, authors, genres, view_count AS ""Views"", score, mal_id, ani_id, created_at, updated_at, alternative_titles
                    FROM manga
                    WHERE @name = ANY(authors)
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";

                var mangaList = (await _postgresService.Connection.QueryAsync<MangaResponse>(
                    selectQuery, new { name, limit = clampedPageSize, offset })).ToList();

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
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve manga by author", ex.Message));
            }
        }

        /// <summary>
        /// Get list of authors
        /// </summary>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of authors.</returns>
        [HttpGet("list")]
        [CacheControl(CacheDuration.OneHour, CacheDuration.TwelveHours)]
        [ProducesResponseType(typeof(SuccessResponse<AuthorListResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetAuthorsList([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 500)] int pageSize = 100)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize, 500, 100);
            var offset = (clampedPage - 1) * clampedPageSize;

            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    WITH author_counts AS (
                        SELECT unnest(authors) as author, COUNT(*) as manga_count
                        FROM manga
                        GROUP BY unnest(authors)
                    ),
                    paginated_authors AS (
                        SELECT author, manga_count, COUNT(*) OVER() as total
                        FROM author_counts
                        ORDER BY manga_count DESC
                        LIMIT @limit OFFSET @offset
                    )
                    SELECT author AS ""Name"", manga_count AS ""MangaCount"", total FROM paginated_authors";

                var rows = (await _postgresService.Connection.QueryAsync(query, new { limit = clampedPageSize, offset })).ToList();

                var authors = rows.Select(r => new AuthorResponse
                {
                    Name = (string)r.Name,
                    MangaCount = (int)r.MangaCount
                }).ToList();

                long totalCount = rows.Count > 0 ? (long)rows[0].total : 0;

                await _postgresService.CloseAsync();

                return Ok(SuccessResponse<AuthorListResponse>.Create(new AuthorListResponse
                {
                    Items = authors,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve authors list", ex.Message));
            }
        }
    }
}
