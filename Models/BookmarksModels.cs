using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public class UpdateBookmarkRequest
    {
        public double? ChapterNumber { get; set; }
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

        [Required]
        public int ScanlatorId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class BatchUpdateBookmarksRequest
    {
        [Required]
        public required List<BatchUpdateBookmarkItem> Items { get; set; } = new();
    }

    public class BatchUpdateBookmarkItem
    {
        [Required]
        public required Guid MangaId { get; set; }

        [Required]
        public required double ChapterNumber { get; set; }
    }

    public class BookmarkResponse
    {
        [Required]
        public Guid BookmarkId { get; set; }
        [Required]
        public DateTimeOffset BookmarkCreatedAt { get; set; }
        [Required]
        public DateTimeOffset BookmarkUpdatedAt { get; set; }
        [Required]
        public Guid MangaId { get; set; }
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
        public MangaType Type { get; set; }
        [Required]
        public string[] Authors { get; set; } = Array.Empty<string>();
        [Required]
        public string[] Genres { get; set; } = Array.Empty<string>();
        [Required]
        public int Views { get; set; } = 0;
        [Required]
        public decimal Score { get; set; } = 0;
        public int? MalId { get; set; }
        public int? AniId { get; set; }
        [Required]
        public string[] AlternativeTitles { get; set; } = Array.Empty<string>();
        [Required]
        public DateTimeOffset MangaCreatedAt { get; set; }
        [Required]
        public DateTimeOffset MangaUpdatedAt { get; set; }
        [Required]
        public MangaChapter LastReadChapter { get; set; } = new MangaChapter();
        [Required]
        public MangaChapter LatestChapter { get; set; } = new MangaChapter();
        [Required]
        public MangaChapter NextChapter { get; set; } = new MangaChapter();
        [Required]
        public int ChaptersBehind { get; set; }
        [JsonIgnore]
        public List<MangaChapter> Chapters { get; set; } = new List<MangaChapter>();
    }

    public class BookmarkListResponse : PaginatedResponse<BookmarkResponse>
    {
    }
}
