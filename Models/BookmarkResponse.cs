using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public class BookmarkChapter
    {
        public Guid Id { get; set; }
        public float Number { get; set; }
        public string? Title { get; set; }
        public short? Pages { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class BookmarkResponse
    {
        public Guid BookmarkId { get; set; }
        public DateTimeOffset BookmarkCreatedAt { get; set; }
        public DateTimeOffset BookmarkUpdatedAt { get; set; }
        public Guid MangaId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Cover { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MangaType Type { get; set; }
        public string[] Authors { get; set; } = Array.Empty<string>();
        public string[] Genres { get; set; } = Array.Empty<string>();
        public int Views { get; set; } = 0;
        public decimal Score { get; set; } = 0;
        public int? MalId { get; set; }
        public int? AniId { get; set; }
        public string[]? AlternativeTitles { get; set; }
        public DateTimeOffset MangaCreatedAt { get; set; }
        public DateTimeOffset MangaUpdatedAt { get; set; }
        public BookmarkChapter LastReadChapter { get; set; } = new BookmarkChapter();
        public List<BookmarkChapter> Chapters { get; set; } = new List<BookmarkChapter>();
    }

    public class BookmarkListResponse : PaginatedResponse<BookmarkResponse>
    {
    }
}
