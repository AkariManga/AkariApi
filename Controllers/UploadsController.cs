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
        private readonly string _bucketName = "uploads";

        public UploadsController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
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
            var existingResponse = await _supabaseService.Client
                .From<UploadDto>()
                .Select("*")
                .Where(u => u.Md5Hash == fileHash)
                .Limit(1)
                .Get();

            var existing = existingResponse.Models.FirstOrDefault();

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
                UserId = userId.ToString(),
                Md5Hash = fileHash,
                Size = processedSize,
                Url = publicUrl,
                UsageCount = 0,
                Tags = tags.Select(t => t.ToLower()).Distinct().ToArray(),
                CreatedAt = DateTime.UtcNow
            };

            await _supabaseService.Client
                .From<UploadDto>()
                .Insert(uploadDto);

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
                var query = _supabaseService.Client
                    .From<UploadDto>();

                var totalCount = await query.Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (clampedPage - 1) * clampedPageSize;

                var uploadsResponse = await query
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var uploadResponses = uploadsResponse.Models.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId,
                    Md5Hash = dto.Md5Hash,
                    Size = dto.Size,
                    Url = dto.Url,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt
                }).ToList();

                var paginatedResponse = new PaginatedResponse<UploadResponse>
                {
                    Items = uploadResponses,
                    TotalItems = totalCount,
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

                var query = _supabaseService.Client
                    .From<UploadDto>()
                    .Where(u => u.UserId == userId.ToString());

                var totalCount = await query.Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (clampedPage - 1) * clampedPageSize;

                var uploadsResponse = await query
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var uploadResponses = uploadsResponse.Models.Select(dto => new UploadResponse
                {
                    Id = dto.Id,
                    UserId = dto.UserId,
                    Md5Hash = dto.Md5Hash,
                    Size = dto.Size,
                    Url = dto.Url,
                    UsageCount = dto.UsageCount,
                    Tags = dto.Tags,
                    CreatedAt = dto.CreatedAt
                }).ToList();

                var paginatedResponse = new PaginatedResponse<UploadResponse>
                {
                    Items = uploadResponses,
                    TotalItems = totalCount,
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