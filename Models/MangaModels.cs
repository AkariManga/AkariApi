using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public enum MangaType
    {
        Manga,
        Manhwa,
        Manhua,
        Other
    }

    public class ChapterOption
    {
        [Required]
        public required string Label { get; set; } = string.Empty;

        [Required]
        public required string Value { get; set; } = string.Empty;
    }

    public class ChapterResponse
    {
        [Required]
        public required Guid Id { get; set; }

        [Required]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required MangaType Type { get; set; } = MangaType.Manga;

        [Required]
        public required short Pages { get; set; }

        [Required]
        public required string Title { get; set; } = string.Empty;

        [Required]
        public required string[] Images { get; set; } = Array.Empty<string>();

        [Required]
        public required float Number { get; set; }

        [Required]
        public required List<ChapterOption> Chapters { get; set; } = new List<ChapterOption>();

        [Required]
        public required Guid MangaId { get; set; }

        [Required]
        public required string MangaTitle { get; set; } = string.Empty;

        public float? LastChapter { get; set; }

        public float? NextChapter { get; set; }

        public int? MalId { get; set; }

        public int? AniId { get; set; }
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
        [Required]
        public double Rank { get; set; }
    }

    public class PopularMangaResponse
    {
        [Required]
        public required Guid Id { get; set; }
        [Required]
        public required string OrigId { get; set; }
        [Required]
        public required string Title { get; set; }
        [Required]
        public required string Cover { get; set; }
        [Required]
        public required string Description { get; set; }
        [Required]
        public required string Status { get; set; }
        [Required]
        public required string Type { get; set; }
        [Required]
        public required string[] Authors { get; set; }
        [Required]
        public required string[] Genres { get; set; }
        [Required]
        public required decimal Score { get; set; }
        public int? MalId { get; set; }
        public int? AniId { get; set; }
        [Required]
        public required DateTimeOffset CreatedAt { get; set; }
        [Required]
        public required DateTimeOffset UpdatedAt { get; set; }
        [Required]
        public required string[] AlternativeTitles { get; set; }
        [Required]
        public required long ViewCount { get; set; }
        [Required]
        public required long TotalCount { get; set; }
    }

    public class RateMangaRequest
    {
        [Required]
        [Range(1, 10)]
        public required int Rating { get; set; }
    }

    public class BatchRateMangaItem
    {
        [Required]
        public required Guid MangaId { get; set; }

        [Required]
        [Range(1, 10)]
        public required int Rating { get; set; }
    }

    public class BatchRateMangaRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(50)]
        public required List<BatchRateMangaItem> Ratings { get; set; } = new List<BatchRateMangaItem>();
    }

    public class ViewMangaRequest
    {
        public bool SaveUserId { get; set; } = false;
    }

    public class BatchGetMangaRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(50)]
        public required List<int> MalIds { get; set; } = new List<int>();
    }

    public class BatchGetAniMangaRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(50)]
        public required List<int> AniIds { get; set; } = new List<int>();
    }

    public class MangaIdsResponse : PaginatedResponse<Guid>
    {
    }

    public class MangaChapterIdsPair
    {
        [Required]
        [JsonPropertyName("mangaId")]
        public required Guid MangaId { get; set; }

        [Required]
        [JsonPropertyName("chapterIds")]
        public required List<float> ChapterIds { get; set; } = new List<float>();
    }

    public class MangaChapterIdsResponse : PaginatedResponse<MangaChapterIdsPair>
    {
    }

    public class AuthorResponse
    {
        [Required]
        public required string Name { get; set; } = string.Empty;

        [Required]
        public required int MangaCount { get; set; }
    }

    public class AuthorListResponse : PaginatedResponse<AuthorResponse>
    {
    }
}