using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class UploadResponse
    {
        [Required]
        public Guid Id { get; set; }
        [Required]
        public string UserId { get; set; } = string.Empty;
        [Required]
        public string Md5Hash { get; set; } = string.Empty;
        [Required]
        public long Size { get; set; }
        [Required]
        public string Url { get; set; } = string.Empty;
        [Required]
        public int UsageCount { get; set; }
        [Required]
        public string[] Tags { get; set; } = Array.Empty<string>();
        [Required]
        public DateTime CreatedAt { get; set; }
    }

    public class UploadRequest
    {
        public IFormFile? File { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }
}