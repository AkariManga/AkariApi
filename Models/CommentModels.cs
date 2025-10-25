using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;

namespace AkariApi.Models
{
    public class CommentResponse
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

    public class CreateCommentRequest
    {
        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public string Content { get; set; } = string.Empty;

        public Guid? ParentId { get; set; }
    }

    public class UpdateCommentRequest
    {
        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public string Content { get; set; } = string.Empty;
    }

    public class VoteCommentRequest
    {
        [Required]
        [Range(-1, 1)]
        public short Value { get; set; }
    }

    public class PaginatedCommentResponse : PaginatedResponse<CommentResponse>
    {
    }

    public class CommentWithRepliesResponse
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
        public List<CommentWithRepliesResponse> Replies { get; set; } = new List<CommentWithRepliesResponse>();
    }
}