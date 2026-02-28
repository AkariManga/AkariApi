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

    public class MangaRatingDistribution
    {
        [JsonPropertyName("1")]
        public int Score1 { get; set; } = 0;

        [JsonPropertyName("2")]
        public int Score2 { get; set; } = 0;

        [JsonPropertyName("3")]
        public int Score3 { get; set; } = 0;

        [JsonPropertyName("4")]
        public int Score4 { get; set; } = 0;

        [JsonPropertyName("5")]
        public int Score5 { get; set; } = 0;

        [JsonPropertyName("6")]
        public int Score6 { get; set; } = 0;

        [JsonPropertyName("7")]
        public int Score7 { get; set; } = 0;

        [JsonPropertyName("8")]
        public int Score8 { get; set; } = 0;

        [JsonPropertyName("9")]
        public int Score9 { get; set; } = 0;

        [JsonPropertyName("10")]
        public int Score10 { get; set; } = 0;
    }

    public class MangaRatingResponse
    {
        [Required]
        public decimal Average { get; set; } = 0;

        [Required]
        public int Total { get; set; } = 0;

        [Required]
        public MangaRatingDistribution Distribution { get; set; } = new MangaRatingDistribution();
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
        public MangaRatingResponse Rating { get; set; } = new MangaRatingResponse();

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