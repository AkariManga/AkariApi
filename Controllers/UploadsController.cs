using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;
using System.Security.Cryptography;
using AkariApi.Services;
using AkariApi.Models;
using AkariApi.Helpers;
using AkariApi.Attributes;
using System.ComponentModel.DataAnnotations;
using Dapper;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/uploads")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class UploadsController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;
        private readonly string _bucketName = "uploads";

        public UploadsController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        private static UploadDto MapUploadRow(dynamic row)
        {
            return new UploadDto
            {
                Id = (Guid)row.id,
                UserId = (Guid)row.user_id,
                Md5Hash = row.md5_hash == null ? null : (string)row.md5_hash,
                Size = (long)row.size,
                Url = row.url == null ? null : (string)row.url,
                UsageCount = (int)row.usage_count,
                Tags = (string[])row.tags,
                CreatedAt = (DateTime)row.created_at,
                Deleted = row.deleted == null ? false : (bool)row.deleted
            };
        }

        [HttpPost()]
        [EnableRateLimiting("uploads")]
        [ProducesResponseType(typeof(SuccessResponse<UploadResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UploadImage([FromForm] UploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest(ErrorResponse.Create("No file uploaded.", status: 400));

            var file = request.File;
            var tags = request.Tags ?? Array.Empty<string>();

            await _supabaseService.InitializeAsync();

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage, 401));
            }

            string fileHash;
            using (var md5 = MD5.Create())
            {
                using (var stream = file.OpenReadStream())
                {
                    fileHash = Convert.ToHexStringLower(md5.ComputeHash(stream));
                    stream.Seek(0, SeekOrigin.Begin);
                }
            }

            UploadDto? existing = null;
            await _postgresService.OpenAsync();
            var existingRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(
                "SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE md5_hash = @hash LIMIT 1",
                new { hash = fileHash });
            if (existingRow != null)
            {
                existing = MapUploadRow(existingRow);
            }
            await _postgresService.CloseAsync();

            string publicUrl;
            long processedSize;

            if (existing != null)
            {
                publicUrl = existing.Url!;
                processedSize = existing.Size;
            }
            else
            {
                using var image = await Image.LoadAsync(file.OpenReadStream());
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(1024, 1024),
                    Mode = ResizeMode.Max
                }));

                using var ms = new MemoryStream();
                await image.SaveAsync(ms, new WebpEncoder { Quality = 80 });
                ms.Seek(0, SeekOrigin.Begin);

                processedSize = ms.Length;

                var uploadResult = await _supabaseService.AdminClient.Storage
                    .From(_bucketName)
                    .Upload(ms.ToArray(), $"{fileHash}.webp", new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true
                    });

                if (string.IsNullOrEmpty(uploadResult))
                    return StatusCode(500, ErrorResponse.Create("Failed to upload file to storage"));

                publicUrl = $"https://img.akarimanga.dpdns.org/{uploadResult}";
            }

            var uploadDto = new UploadDto
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Md5Hash = fileHash,
                Size = processedSize,
                Url = publicUrl,
                UsageCount = 0,
                Tags = tags.Select(t => t.ToLower()).Distinct().ToArray(),
                CreatedAt = DateTime.UtcNow
            };

            await _postgresService.OpenAsync();
            await _postgresService.Connection.ExecuteAsync(
                "INSERT INTO uploads (id, user_id, md5_hash, size, url, usage_count, tags, created_at) VALUES (@id, @userId, @hash, @size, @url, @usage, @tags, @created)",
                new { id = uploadDto.Id, userId = uploadDto.UserId, hash = uploadDto.Md5Hash, size = uploadDto.Size, url = uploadDto.Url, usage = uploadDto.UsageCount, tags = uploadDto.Tags, created = uploadDto.CreatedAt });
            await _postgresService.CloseAsync();

            var uploadResponse = new UploadResponse
            {
                Id = uploadDto.Id,
                UserId = uploadDto.UserId,
                Md5Hash = uploadDto.Md5Hash,
                Size = uploadDto.Size,
                Url = uploadDto.Url,
                UsageCount = uploadDto.UsageCount,
                Tags = uploadDto.Tags,
                CreatedAt = uploadDto.CreatedAt,
                Deleted = false
            };

            return Ok(SuccessResponse<UploadResponse>.Create(uploadResponse));
        }

        [HttpGet]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<PaginatedResponse<UploadResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUploads(
            [FromQuery] string? query = null,
            [FromQuery, Range(1, int.MaxValue)] int page = 1,
            [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                long totalCount;
                if (!string.IsNullOrWhiteSpace(query))
                {
                    totalCount = await _postgresService.Connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM uploads WHERE deleted = FALSE AND search_vector @@ plainto_tsquery('english', @query)",
                        new { query });
                }
                else
                {
                    totalCount = await _postgresService.Connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(*) FROM uploads WHERE deleted = FALSE");
                }

                var offset = (clampedPage - 1) * clampedPageSize;
                IEnumerable<dynamic> rows;

                if (!string.IsNullOrWhiteSpace(query))
                {
                    rows = await _postgresService.Connection.QueryAsync(@"
                        SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted
                        FROM uploads
                        WHERE deleted = FALSE AND search_vector @@ plainto_tsquery('english', @query)
                        ORDER BY ts_rank(search_vector, plainto_tsquery('english', @query)) DESC
                        OFFSET @offset LIMIT @limit",
                        new { query, offset, limit = clampedPageSize });
                }
                else
                {
                    rows = await _postgresService.Connection.QueryAsync(
                        "SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE deleted = FALSE ORDER BY created_at DESC OFFSET @offset LIMIT @limit",
                        new { offset, limit = clampedPageSize });
                }

                var uploads = rows.Select(r => MapUploadRow(r)).ToList();

                await _postgresService.CloseAsync();

                var uploadResponses = uploads.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId!,
                    Md5Hash = dto.Md5Hash,
                    Size = dto.Size,
                    Url = dto.Url,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt,
                    Deleted = dto.Deleted
                }).ToList();

                var paginatedResponse = new PaginatedResponse<UploadResponse>
                {
                    Items = uploadResponses,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                };

                return Ok(SuccessResponse<PaginatedResponse<UploadResponse>>.Create(paginatedResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("An error occurred while retrieving uploads", ex.Message));
            }
        }

        [HttpGet("me")]
        [CacheControl(CacheDuration.TenMinutes, CacheDuration.FiveMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<PaginatedResponse<UploadResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMyUploads([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(error))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", error, 401));
                }

                await _postgresService.OpenAsync();

                long totalCount = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM uploads WHERE user_id = @userId AND deleted = FALSE",
                    new { userId });

                var offset = (clampedPage - 1) * clampedPageSize;
                var rows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE user_id = @userId AND deleted = FALSE ORDER BY created_at DESC OFFSET @offset LIMIT @limit",
                    new { userId, offset, limit = clampedPageSize });

                var uploads = rows.Select(r => MapUploadRow(r)).ToList();

                await _postgresService.CloseAsync();

                var uploadResponses = uploads.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId!,
                    Md5Hash = dto.Md5Hash,
                    Size = dto.Size,
                    Url = dto.Url,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt,
                    Deleted = dto.Deleted
                }).ToList();

                var paginatedResponse = new PaginatedResponse<UploadResponse>
                {
                    Items = uploadResponses,
                    TotalItems = (int)totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                };

                return Ok(SuccessResponse<PaginatedResponse<UploadResponse>>.Create(paginatedResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("An error occurred while retrieving uploads", ex.Message));
            }
        }

        [HttpPost("batch")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<List<UploadResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetBatchUploads([FromBody] BatchUploadRequest request)
        {
            if (request.Ids == null || request.Ids.Count == 0 || request.Ids.Count > 100)
                return BadRequest(ErrorResponse.Create("Invalid request. Provide 1-100 IDs.", status: 400));

            try
            {
                await _postgresService.OpenAsync();

                var rows = await _postgresService.Connection.QueryAsync(
                    "SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE id = ANY(@ids) AND deleted = FALSE",
                    new { ids = request.Ids.ToArray() });

                var uploads = rows.Select(r => MapUploadRow(r)).ToList();

                await _postgresService.CloseAsync();

                var uploadResponses = uploads.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId,
                    Md5Hash = dto.Md5Hash,
                    Size = dto.Size,
                    Url = dto.Url,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt,
                    Deleted = dto.Deleted
                }).ToList();

                return Ok(SuccessResponse<List<UploadResponse>>.Create(uploadResponses));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("An error occurred while retrieving uploads", ex.Message));
            }
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> DeleteUpload([FromRoute] Guid id)
        {
            await _supabaseService.InitializeAsync();

            var (userId, error) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(error))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", error, 401));
            }

            Guid uploadUserId = Guid.Empty;
            string? md5Hash = null;
            int usageCount = 0;
            bool isDeleted = false;

            try
            {
                await _postgresService.OpenAsync();

                var infoRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(
                    "SELECT user_id, md5_hash, usage_count, deleted FROM uploads WHERE id = @id LIMIT 1",
                    new { id });

                if (infoRow != null)
                {
                    uploadUserId = (Guid)infoRow.user_id;
                    md5Hash = infoRow.md5_hash == null ? null : (string)infoRow.md5_hash;
                    usageCount = (int)infoRow.usage_count;
                    isDeleted = (bool)infoRow.deleted;
                }

                if (md5Hash == null)
                {
                    return NotFound(ErrorResponse.Create("Upload not found", "Upload does not exist", 404));
                }

                if (isDeleted)
                {
                    return NotFound(ErrorResponse.Create("Upload not found", "Upload already deleted", 404));
                }

                if (uploadUserId != userId)
                {
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You do not own this upload", 403));
                }

                long dupeCount = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM uploads WHERE md5_hash = @hash AND id <> @id",
                    new { hash = md5Hash, id });
                bool hasDeduplicates = dupeCount > 0;

                if (usageCount > 0)
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "UPDATE uploads SET deleted = TRUE, url = NULL, md5_hash = NULL WHERE id = @id",
                        new { id });
                }
                else
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "DELETE FROM uploads WHERE id = @id",
                        new { id });
                }

                if (usageCount == 0 && !hasDeduplicates)
                {
                    await _supabaseService.AdminClient.Storage
                        .From(_bucketName)
                        .Remove(new List<string> { $"{md5Hash}.webp" });
                }

                var message = usageCount > 0 ? "Upload marked as deleted." : "Upload deleted.";
                return Ok(SuccessResponse<string>.Create(message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("An error occurred while deleting the upload", ex.Message));
            }
            finally
            {
                await _postgresService.CloseAsync();
            }
        }
    }
}
