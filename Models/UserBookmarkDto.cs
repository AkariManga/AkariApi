using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("user_bookmarks")]
    public class UserBookmarkDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("manga_id")]
        public Guid MangaId { get; set; }

        [Column("last_read_chapter_id")]
        public Guid? LastReadChapterId { get; set; }

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class UpdateBookmarkRequest
    {
        public Guid ChapterId { get; set; }
    }

    public class LastReadResponse
    {
        [Required]
        public Guid Id { get; set; }

        [Required]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MangaType Type { get; set; } = MangaType.Manga;

        [Required]
        public short Pages { get; set; }

        [Required]
        public string Title { get; set; } = string.Empty;

        [Required]
        public float Number { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}