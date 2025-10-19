using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public enum MangaType
    {
        Manga,
        Manwha,
        Manhua,
        OEL
    }

    [Table("manga")]
    public class MangaDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("orig_id")]
        public string OrigId { get; set; } = string.Empty;

        [Column("title")]
        public string Title { get; set; } = string.Empty;

        [Column("cover")]
        public string Cover { get; set; } = string.Empty;

        [Column("description")]
        public string Description { get; set; } = string.Empty;

        [Column("status")]
        public string Status { get; set; } = string.Empty;

        [Column("type")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MangaType Type { get; set; } = MangaType.Manga;

        [Column("search_vector")]
        public string SearchVector { get; set; } = string.Empty;

        [Column("authors")]
        public string[] Authors { get; set; } = Array.Empty<string>();

        [Column("genres")]
        public string[] Genres { get; set; } = Array.Empty<string>();

        [Column("mal_id")]
        public int? MalId { get; set; }

        [Column("ani_id")]
        public int? AniId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("alternative_titles")]
        public string[]? AlternativeTitles { get; set; }
    }

    public class MangaResponse
    {
        [Required]
        public Guid Id { get; set; }

        [Required]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Cover { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty;

        [Required]
        public string Status { get; set; } = string.Empty;

        [Required]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MangaType Type { get; set; } = MangaType.Manga;

        [Required]
        public string[] Authors { get; set; } = Array.Empty<string>();

        [Required]
        public string[] Genres { get; set; } = Array.Empty<string>();

        public string[]? AlternativeTitles { get; set; }

        public int? MalId { get; set; }

        public int? AniId { get; set; }

        [Required]
        public DateTimeOffset CreatedAt { get; set; }

        [Required]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class MangaListResponse : PaginatedResponse<MangaResponse>
    {
    }

    public class MangaChapter
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public float Number { get; set; }
        public short? Pages { get; set; }
    }

    public class MangaDetailResponse : MangaResponse
    {
        public List<MangaChapter> Chapters { get; set; } = [];
    }
}