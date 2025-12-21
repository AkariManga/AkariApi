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

        [Column("view_count")]
        public int Views { get; set; }

        [Column("score")]
        public decimal Score { get; set; }

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

    [Table("manga")]
    public class MangaWithChaptersDto : MangaDto
    {
        public List<ChapterDto> Chapters { get; set; } = new List<ChapterDto>();
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

        [Required]
        public int Views { get; set; } = 0;

        [Required]
        public decimal Score { get; set; } = 0;

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
        [Required]
        public Guid Id { get; set; }

        [Required]
        public string Title { get; set; } = string.Empty;

        [Required]
        public float Number { get; set; }

        [Required]
        public short Pages { get; set; }

        [Required]
        public DateTimeOffset CreatedAt { get; set; }

        [Required]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class MangaDetailResponse : MangaResponse
    {
        [Required]
        public List<MangaChapter> Chapters { get; set; } = new List<MangaChapter>();
    }

    public class MangaSearchResponse : MangaResponse
    {
        public double Rank { get; set; }
    }

    [Table("manga_ratings")]
    public class MangaRatingDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("manga_id")]
        public Guid MangaId { get; set; }

        [Column("rating")]
        public int Rating { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class RateMangaRequest
    {
        [Required]
        [Range(1, 10)]
        public int Rating { get; set; }
    }

    public class BatchGetMangaRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(50)]
        public List<int> MalIds { get; set; } = new List<int>();
    }

    public class MangaIdsResponse : PaginatedResponse<Guid>
    {
    }

    public class MangaChapterIdsPair
    {
        [Required]
        [JsonPropertyName("mangaId")]
        public Guid MangaId { get; set; }

        [Required]
        [JsonPropertyName("chapterIds")]
        public List<float> ChapterIds { get; set; } = new List<float>();
    }

    public class MangaChapterIdsResponse : PaginatedResponse<MangaChapterIdsPair>
    {
    }

    public class AuthorResponse
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public int MangaCount { get; set; }
    }

    public class AuthorListResponse : PaginatedResponse<AuthorResponse>
    {
    }
}