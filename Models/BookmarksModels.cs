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
        [Required]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        [Required]
        public Guid UserId { get; set; }

        [Column("manga_id")]
        [Required]
        public Guid MangaId { get; set; }

        [Column("last_read_chapter_id")]
        public Guid? LastReadChapterId { get; set; }

        [Column("updated_at")]
        [Required]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("created_at")]
        [Required]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    [Table("user_bookmarks_unread")]
    public class UserBookmarkUnreadDto : BaseModel
    {
        [PrimaryKey("id")]
        [Required]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        [Required]
        public Guid UserId { get; set; }

        [Column("manga_id")]
        [Required]
        public Guid MangaId { get; set; }

        [Column("last_read_chapter_id")]
        public Guid? LastReadChapterId { get; set; }

        [Column("updated_at")]
        [Required]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("created_at")]
        [Required]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class UpdateBookmarkRequest
    {
        [Required]
        public required double ChapterNumber { get; set; }
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

    public class BookmarkChapter
    {
        [Required]
        public Guid Id { get; set; }
        [Required]
        public float Number { get; set; }
        [Required]
        public string Title { get; set; } = string.Empty;
        [Required]
        public short Pages { get; set; }
        [Required]
        public DateTimeOffset CreatedAt { get; set; }
        [Required]
        public DateTimeOffset UpdatedAt { get; set; }
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
        public BookmarkChapter LastReadChapter { get; set; } = new BookmarkChapter();
        [Required]
        public BookmarkChapter LatestChapter { get; set; } = new BookmarkChapter();
        [Required]
        public BookmarkChapter NextChapter { get; set; } = new BookmarkChapter();
        [Required]
        public int ChaptersBehind { get; set; }
        [JsonIgnore]
        public List<BookmarkChapter> Chapters { get; set; } = new List<BookmarkChapter>();
    }

    public class BookmarkListResponse : PaginatedResponse<BookmarkResponse>
    {
    }
}
