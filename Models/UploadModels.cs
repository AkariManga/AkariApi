using System.ComponentModel.DataAnnotations;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("uploads")]
    public class UploadDto : BaseModel
    {
        [PrimaryKey("id")]
        [Required]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        [Required]
        public Guid UserId { get; set; }

        [Column("md5_hash")]
        public string? Md5Hash { get; set; }

        [Column("size")]
        [Required]
        public long Size { get; set; }

        [Column("url")]
        public string? Url { get; set; }

        [Column("usage_count")]
        [Required]
        public int UsageCount { get; set; }

        [Column("tags")]
        [Required]
        public string[] Tags { get; set; } = Array.Empty<string>();

        [Column("search_vector")]
        [Required]
        public string SearchVector { get; set; } = string.Empty;

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; }

        [Column("deleted")]
        [Required]
        public bool Deleted { get; set; } = false;
    }

    public class UploadResponse
    {
        [Required]
        public required Guid Id { get; set; }
        [Required]
        public required Guid UserId { get; set; }
        public string? Md5Hash { get; set; }
        [Required]
        public required long Size { get; set; }
        public string? Url { get; set; }
        [Required]
        public required int UsageCount { get; set; }
        [Required]
        public required string[] Tags { get; set; } = Array.Empty<string>();
        [Required]
        public required DateTime CreatedAt { get; set; }
        [Required]
        public required bool Deleted { get; set; }
    }

    public class UploadRequest
    {
        public IFormFile? File { get; set; }
        [Required]
        public required string[] Tags { get; set; } = Array.Empty<string>();
    }
}