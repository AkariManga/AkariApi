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
using Npgsql;

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

        [HttpPost()]
        [EnableRateLimiting("uploads")]
        [ProducesResponseType(typeof(SuccessResponse<UploadResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UploadImage([FromForm] UploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest(ErrorResponse.Create("No file uploaded."));

            var file = request.File;
            var tags = request.Tags ?? Array.Empty<string>();

            await _supabaseService.InitializeAsync();

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
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

            // Check if a file with this hash already exists
            UploadDto? existing = null;
            await _postgresService.OpenAsync();
            using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE md5_hash = @hash LIMIT 1", _postgresService.Connection))
            {
                cmd.Parameters.AddWithValue("hash", fileHash);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        existing = new UploadDto
                        {
                            Id = reader.GetGuid(0),
                            UserId = Guid.Parse(reader.GetString(1)),
                            Md5Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                            Size = reader.GetInt64(3),
                            Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                            UsageCount = reader.GetInt32(5),
                            Tags = reader.GetFieldValue<string[]>(6),
                            CreatedAt = reader.GetDateTime(7)
                        };
                    }
                }
            }
            await _postgresService.CloseAsync();

            string publicUrl;
            long processedSize;

            if (existing != null)
            {
                // Reuse the URL and processed size for duplicate
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

                // Upload to Supabase Storage
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
            using (var cmd = new NpgsqlCommand("INSERT INTO uploads (id, user_id, md5_hash, size, url, usage_count, tags, created_at) VALUES (@id, @userId, @hash, @size, @url, @usage, @tags, @created)", _postgresService.Connection))
            {
                cmd.Parameters.AddWithValue("id", uploadDto.Id);
                cmd.Parameters.AddWithValue("userId", uploadDto.UserId);
                cmd.Parameters.AddWithValue("hash", uploadDto.Md5Hash);
                cmd.Parameters.AddWithValue("size", uploadDto.Size);
                cmd.Parameters.AddWithValue("url", uploadDto.Url);
                cmd.Parameters.AddWithValue("usage", uploadDto.UsageCount);
                cmd.Parameters.AddWithValue("tags", uploadDto.Tags);
                cmd.Parameters.AddWithValue("created", uploadDto.CreatedAt);
                await cmd.ExecuteNonQueryAsync();
            }
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
                    using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE deleted = FALSE AND search_vector @@ plainto_tsquery('english', @query)", _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("query", query);
                        totalCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                    }
                }
                else
                {
                    using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE deleted = FALSE", _postgresService.Connection))
                    {
                        totalCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                    }
                }

                var offset = (clampedPage - 1) * clampedPageSize;
                var uploads = new List<UploadDto>();

                if (!string.IsNullOrWhiteSpace(query))
                {
                    var searchQuery = @"
                        SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted
                        FROM uploads
                        WHERE deleted = FALSE AND search_vector @@ plainto_tsquery('english', @query)
                        ORDER BY ts_rank(search_vector, plainto_tsquery('english', @query)) DESC
                        OFFSET @offset LIMIT @limit";
                    using (var cmd = new NpgsqlCommand(searchQuery, _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("query", query);
                        cmd.Parameters.AddWithValue("offset", offset);
                        cmd.Parameters.AddWithValue("limit", clampedPageSize);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                uploads.Add(new UploadDto
                                {
                                    Id = reader.GetGuid(0),
                                    UserId = reader.GetGuid(1),
                                    Md5Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    Size = reader.GetInt64(3),
                                    Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                                    UsageCount = reader.GetInt32(5),
                                    Tags = reader.GetFieldValue<string[]>(6),
                                    CreatedAt = reader.GetDateTime(7)
                                });
                            }
                        }
                    }
                }
                else
                {
                    using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE deleted = FALSE ORDER BY created_at DESC OFFSET @offset LIMIT @limit", _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("offset", offset);
                        cmd.Parameters.AddWithValue("limit", clampedPageSize);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                uploads.Add(new UploadDto
                                {
                                    Id = reader.GetGuid(0),
                                    UserId = reader.GetGuid(1),
                                    Md5Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    Size = reader.GetInt64(3),
                                    Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                                    UsageCount = reader.GetInt32(5),
                                    Tags = reader.GetFieldValue<string[]>(6),
                                    CreatedAt = reader.GetDateTime(7),
                                    Deleted = reader.GetBoolean(8)
                                });
                            }
                        }
                    }
                }

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

                long totalCount;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE user_id = @userId AND deleted = FALSE", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    totalCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                }

                var offset = (clampedPage - 1) * clampedPageSize;
                var uploads = new List<UploadDto>();

                using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE user_id = @userId AND deleted = FALSE ORDER BY created_at DESC OFFSET @offset LIMIT @limit", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("offset", offset);
                    cmd.Parameters.AddWithValue("limit", clampedPageSize);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            uploads.Add(new UploadDto
                            {
                                Id = reader.GetGuid(0),
                                UserId = reader.GetGuid(1),
                                Md5Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Size = reader.GetInt64(3),
                                Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                                UsageCount = reader.GetInt32(5),
                                Tags = reader.GetFieldValue<string[]>(6),
                                CreatedAt = reader.GetDateTime(7),
                                Deleted = reader.GetBoolean(8)
                            });
                        }
                    }
                }

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
                return BadRequest(ErrorResponse.Create("Invalid request. Provide 1-100 IDs."));

            try
            {
                await _postgresService.OpenAsync();

                var uploads = new List<UploadDto>();

                using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at, deleted FROM uploads WHERE id = ANY(@ids) AND deleted = FALSE", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("ids", request.Ids.ToArray());
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            uploads.Add(new UploadDto
                            {
                                Id = reader.GetGuid(0),
                                UserId = reader.GetGuid(1),
                                Md5Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Size = reader.GetInt64(3),
                                Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                                UsageCount = reader.GetInt32(5),
                                Tags = reader.GetFieldValue<string[]>(6),
                                CreatedAt = reader.GetDateTime(7),
                                Deleted = reader.GetBoolean(8)
                            });
                        }
                    }
                }

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

                using (var cmd = new NpgsqlCommand("SELECT user_id, md5_hash, usage_count, deleted FROM uploads WHERE id = @id LIMIT 1", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", id);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            uploadUserId = reader.GetGuid(0);
                            md5Hash = reader.IsDBNull(1) ? null : reader.GetString(1);
                            usageCount = reader.GetInt32(2);
                            isDeleted = reader.GetBoolean(3);
                        }
                    }
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

                bool hasDeduplicates = false;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE md5_hash = @hash AND id <> @id", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("hash", md5Hash);
                    cmd.Parameters.AddWithValue("id", id);
                    var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                    hasDeduplicates = count > 0;
                }

                if (usageCount > 0)
                {
                    using (var cmd = new NpgsqlCommand("UPDATE uploads SET deleted = TRUE, url = NULL, md5_hash = NULL WHERE id = @id", _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    using (var cmd = new NpgsqlCommand("DELETE FROM uploads WHERE id = @id", _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }
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