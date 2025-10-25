namespace AkariApi.Models
{
    public class UploadResponse
    {
        public Guid Id { get; set; }
        public string? UserId { get; set; }
        public string? Md5Hash { get; set; }
        public long Size { get; set; }
        public string? Url { get; set; }
        public int UsageCount { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; set; }
    }

    public class UploadRequest
    {
        public IFormFile? File { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }
}