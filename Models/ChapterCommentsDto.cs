using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("chapter_comments")]
    public class ChapterCommentDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("chapter_id")]
        public Guid ChapterId { get; set; }

        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("parent_id")]
        public Guid? ParentId { get; set; }

        [Column("content")]
        public string Content { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("edited")]
        public bool Edited { get; set; }

        [Column("deleted")]
        public bool Deleted { get; set; }

        [Column("upvotes")]
        public int Upvotes { get; set; }

        [Column("downvotes")]
        public int Downvotes { get; set; }
    }

    [Table("chapter_comment_votes")]
    public class ChapterCommentVoteDto : BaseModel
    {
        [PrimaryKey("comment_id")]
        [Column("comment_id")]
        public Guid CommentId { get; set; }

        [PrimaryKey("user_id")]
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("value")]
        public short Value { get; set; }
    }

    public class CommentWithReplyCountDto
    {
        public Guid Id { get; set; }
        public Guid ChapterId { get; set; }
        public Guid UserId { get; set; }
        public Guid? ParentId { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool Edited { get; set; }
        public bool Deleted { get; set; }
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public long ReplyCount { get; set; }
    }
}