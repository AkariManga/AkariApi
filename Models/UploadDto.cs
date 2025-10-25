using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("uploads")]
    public class UploadDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        public string? UserId { get; set; }

        [Column("md5_hash")]
        public string? Md5Hash { get; set; }

        [Column("size")]
        public long Size { get; set; }

        [Column("url")]
        public string? Url { get; set; }

        [Column("usage_count")]
        public int UsageCount { get; set; }

        [Column("tags")]
        public string[] Tags { get; set; } = Array.Empty<string>();

        [Column("search_vector")]
        public string SearchVector { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}