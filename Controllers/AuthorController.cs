using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Npgsql;

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

                // Get total count
                var countQuery = "SELECT COUNT(*) FROM manga WHERE @name = ANY(authors)";
                long totalCountLong;
                using (var countCmd = new NpgsqlCommand(countQuery, _postgresService.Connection))
                {
                    countCmd.Parameters.AddWithValue("name", name);
                    var result = await countCmd.ExecuteScalarAsync();
                    totalCountLong = result != null ? (long)result : 0;
                }
                // Clamp to int.MaxValue to avoid overflow; adjust as needed
                int totalCount = totalCountLong > int.MaxValue ? int.MaxValue : (int)totalCountLong;

                // Get paginated results
                var offset = (clampedPage - 1) * clampedPageSize;
                var selectQuery = @"
                    SELECT id, orig_id, title, cover, description, status, type, search_vector, authors, genres, view_count, score, mal_id, ani_id, created_at, updated_at, alternative_titles
                    FROM manga
                    WHERE @name = ANY(authors)
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";
                var mangaList = new List<MangaResponse>();
                using (var selectCmd = new NpgsqlCommand(selectQuery, _postgresService.Connection))
                {
                    selectCmd.Parameters.AddWithValue("name", name);
                    selectCmd.Parameters.AddWithValue("limit", clampedPageSize);
                    selectCmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await selectCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var manga = new MangaResponse
                            {
                                Id = reader.GetGuid(0),
                                Title = reader.GetString(2),
                                Cover = reader.GetString(3),
                                Description = reader.GetString(4),
                                Status = reader.GetString(5),
                                Type = Enum.Parse<MangaType>(reader.GetString(6)),
                                Authors = (string[])reader.GetValue(8),
                                Genres = (string[])reader.GetValue(9),
                                Views = reader.GetInt64(10) > int.MaxValue ? int.MaxValue : (int)reader.GetInt64(10),  // Safe cast with clamp
                                Score = reader.GetDecimal(11),
                                MalId = reader.IsDBNull(12) ? null : (reader.GetInt64(12) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(12)),
                                AniId = reader.IsDBNull(13) ? null : (reader.GetInt64(13) > int.MaxValue ? int.MaxValue : (int?)reader.GetInt64(13)),
                                CreatedAt = reader.GetDateTime(14),
                                UpdatedAt = reader.GetDateTime(15),
                                AlternativeTitles = reader.IsDBNull(16) ? null : (string[])reader.GetValue(16)
                            };
                            mangaList.Add(manga);
                        }
                    }
                }

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
                    SELECT author, manga_count, total FROM paginated_authors";
                var authors = new List<AuthorResponse>();
                long totalCount = 0;
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("offset", offset);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var author = new AuthorResponse
                            {
                                Name = reader.GetString(0),
                                MangaCount = reader.GetInt32(1)
                            };
                            if (totalCount == 0)
                            {
                                totalCount = reader.GetInt64(2);
                            }
                            authors.Add(author);
                        }
                    }
                }

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