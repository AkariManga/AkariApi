using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public enum CommentReportReason
    {
        [EnumMember(Value = "spam")]
        spam,
        [EnumMember(Value = "harassment")]
        harassment,
        [EnumMember(Value = "inappropriate")]
        inappropriate,
        [EnumMember(Value = "hate-speech")]
        hate_speech,
        [EnumMember(Value = "other")]
        other
    }

    public class UserProfile
    {
        [Required]
        public Guid Id { get; set; }
        [Required]
        public string Username { get; set; } = string.Empty;
        [Required]
        public string DisplayName { get; set; } = string.Empty;
        [Required]
        public string Role { get; set; } = "user";
        [Required]
        public bool Banned { get; set; } = false;
    }

    public class BaseCommentResponse
    {
        [Required]
        public Guid Id { get; set; }
        [Required]
        public string TargetType { get; set; } = string.Empty;
        [Required]
        public Guid TargetId { get; set; }
        [Required]
        public UserProfile UserProfile { get; set; } = new UserProfile();
        public Guid? ParentId { get; set; }
        [Required]
        public string Content { get; set; } = string.Empty;
        [Required]
        public DateTimeOffset CreatedAt { get; set; }
        [Required]
        public DateTimeOffset UpdatedAt { get; set; }
        [Required]
        public bool Edited { get; set; }
        [Required]
        public bool Deleted { get; set; }
        [Required]
        public int Upvotes { get; set; }
        [Required]
        public int Downvotes { get; set; }
        public UploadResponse? Attachment { get; set; }
    }

    public class CommentResponse : BaseCommentResponse
    {
        [Required]
        public long ReplyCount { get; set; }
    }

    public class CreateCommentRequest
    {
        [Required]
        public required string TargetType { get; set; } = string.Empty;

        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public required string Content { get; set; } = string.Empty;

        public Guid? ParentId { get; set; }

        public Guid? AttachmentId { get; set; }
    }

    public class UpdateCommentRequest
    {
        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public required string Content { get; set; } = string.Empty;
    }

    public class VoteCommentRequest
    {
        [Required]
        [Range(-1, 1)]
        public required short Value { get; set; }
    }

    public class ReportCommentRequest
    {
        [Required]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required CommentReportReason Reason { get; set; }

        [StringLength(500)]
        public string? Description { get; set; }
    }

    public class CommentVoteResponse
    {
        [Required]
        public required Guid CommentId { get; set; }
        [Required]
        public required short Value { get; set; }
        [Required]
        public required Guid TargetId { get; set; }
    }

    public class PaginatedCommentResponse : PaginatedResponse<CommentResponse>
    {
    }

    public class CommentWithRepliesResponse : BaseCommentResponse
    {
        [Required]
        public List<CommentWithRepliesResponse> Replies { get; set; } = new List<CommentWithRepliesResponse>();
    }
}