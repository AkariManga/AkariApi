using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("comments")]
    public class CommentDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("target_type")]
        public string TargetType { get; set; } = string.Empty;

        [Column("target_id")]
        public Guid TargetId { get; set; }

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

        [Column("attachment_id")]
        public Guid? AttachmentId { get; set; }

        public ProfileDto Profiles { get; set; } = new ProfileDto();

        public UploadDto Uploads { get; set; } = new UploadDto();
    }

    [Table("comment_votes")]
    public class CommentVoteDto : BaseModel
    {
        [PrimaryKey("comment_id")]
        [Column("comment_id")]
        public Guid CommentId { get; set; }

        [PrimaryKey("user_id")]
        [Column("user_id")]
        public Guid UserId { get; set; }

        [Column("value")]
        public short Value { get; set; }

        public CommentDto Comments { get; set; } = new CommentDto();
    }
}