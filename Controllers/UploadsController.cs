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
    [RequireTokenRefresh]
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
            using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at FROM uploads WHERE md5_hash = @hash LIMIT 1", _postgresService.Connection))
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
                            Url = reader.GetString(4),
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
                CreatedAt = uploadDto.CreatedAt
            };

            return Ok(SuccessResponse<UploadResponse>.Create(uploadResponse));
        }

        [HttpGet]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.FiveMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<PaginatedResponse<UploadResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUploads([FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _postgresService.OpenAsync();

                long totalCount;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads", _postgresService.Connection))
                {
                    totalCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                }

                var offset = (clampedPage - 1) * clampedPageSize;
                var uploads = new List<UploadDto>();

                using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at FROM uploads ORDER BY created_at DESC OFFSET @offset LIMIT @limit", _postgresService.Connection))
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
                                Md5Hash = reader.GetString(2),
                                Size = reader.GetInt64(3),
                                Url = reader.GetString(4),
                                UsageCount = reader.GetInt32(5),
                                Tags = reader.GetFieldValue<string[]>(6),
                                CreatedAt = reader.GetDateTime(7)
                            });
                        }
                    }
                }

                await _postgresService.CloseAsync();

                var uploadResponses = uploads.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId!,
                    Md5Hash = dto.Md5Hash!,
                    Size = dto.Size,
                    Url = dto.Url!,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt
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
        [RequireTokenRefresh]
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
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    totalCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                }

                var offset = (clampedPage - 1) * clampedPageSize;
                var uploads = new List<UploadDto>();

                using (var cmd = new NpgsqlCommand("SELECT id, user_id, md5_hash, size, url, usage_count, tags, created_at FROM uploads WHERE user_id = @userId ORDER BY created_at DESC OFFSET @offset LIMIT @limit", _postgresService.Connection))
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
                                Md5Hash = reader.GetString(2),
                                Size = reader.GetInt64(3),
                                Url = reader.GetString(4),
                                UsageCount = reader.GetInt32(5),
                                Tags = reader.GetFieldValue<string[]>(6),
                                CreatedAt = reader.GetDateTime(7)
                            });
                        }
                    }
                }

                await _postgresService.CloseAsync();

                var uploadResponses = uploads.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId!,
                    Md5Hash = dto.Md5Hash!,
                    Size = dto.Size,
                    Url = dto.Url!,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt
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
    }
}