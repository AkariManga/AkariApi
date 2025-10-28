using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    [Table("chapters")]
    public class ChapterDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; }

        [Column("manga_id")]
        public Guid MangaId { get; set; }

        [Column("number")]
        public float Number { get; set; }

        [Column("title")]
        public string Title { get; set; } = string.Empty;

        [Column("pages")]
        public short Pages { get; set; }

        [Column("images")]
        public string[] Images { get; set; } = Array.Empty<string>();

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class ChapterOption
    {
        [Required]
        public string Label { get; set; } = string.Empty;

        [Required]
        public string Value { get; set; } = string.Empty;
    }

    public class ChapterResponse
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
        public string[] Images { get; set; } = Array.Empty<string>();

        [Required]
        public float Number { get; set; }

        [Required]
        public List<ChapterOption> Chapters { get; set; } = new List<ChapterOption>();

        [Required]
        public Guid MangaId { get; set; }

        [Required]
        public string MangaTitle { get; set; } = string.Empty;

        public float? LastChapter { get; set; }

        public float? NextChapter { get; set; }

        public int? MalId { get; set; }

        public int? AniId { get; set; }
    }
}