using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/comments")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class CommentsController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public CommentsController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        /// <summary>
        /// Get comments for a target
        /// </summary>
        /// <param name="id">The comment target ID.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <returns>A paginated list of top-level comments for the target.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(ApiResponse<PaginatedCommentResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetComments(Guid id, int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);

            try
            {
                await _supabaseService.InitializeAsync();

                var totalCount = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.ParentId == null)
                    .Where(c => c.TargetId == id)
                    .Count(Supabase.Postgrest.Constants.CountType.Exact);
                var offset = (clampedPage - 1) * clampedPageSize;

                if (totalCount == 0)
                {
                    return NotFound(ApiResponse<ErrorData>.Error("No comments found for the specified target.", status: 404));
                }

                var response = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.ParentId == null)
                    .Where(c => c.TargetId == id)
                    .Select("*, profiles(*), uploads(*), (upvotes - downvotes) as score")
                    .Order("score", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Offset(offset)
                    .Limit(clampedPageSize)
                    .Get();

                var comments = response.Models.Select(c => new CommentResponse
                {
                    Id = c.Id,
                    TargetType = c.TargetType,
                    TargetId = c.TargetId,
                    UserProfile = new UserProfile
                    {
                        Id = c.UserId,
                        Username = c.Profiles.Username,
                        DisplayName = c.Profiles.DisplayName,
                    },
                    ParentId = c.ParentId,
                    Content = c.Content,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Edited = c.Edited,
                    Deleted = c.Deleted,
                    Upvotes = c.Upvotes,
                    Downvotes = c.Downvotes,
                    Attachment = new UploadResponse
                    {
                        Id = c.Uploads.Id,
                        UserId = c.Uploads.UserId,
                        Md5Hash = c.Uploads.Md5Hash,
                        Size = c.Uploads.Size,
                        Url = c.Uploads.Url,
                        UsageCount = c.Uploads.UsageCount,
                        Tags = c.Uploads.Tags,
                        CreatedAt = c.Uploads.CreatedAt
                    },
                }).ToList();

                return Ok(ApiResponse<PaginatedCommentResponse>.Success(new PaginatedCommentResponse
                {
                    Items = comments,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("An error occurred while fetching comments.", ex.Message));
            }
        }

        /// <summary>
        /// Get user's votes on comments in a target
        /// </summary>
        /// <param name="id">The comment target ID.</param>
        /// <returns>A list of the user's votes on comments in the target.</returns>
        [HttpGet("{id}/votes")]
        [ProducesResponseType(typeof(ApiResponse<List<CommentVoteResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        [RequireTokenRefresh]
        public async Task<IActionResult> GetUserCommentVotes(Guid id)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .From<CommentVoteDto>()
                    .Where(cv => cv.UserId == userId)
                    .Select("comment_id, value, comments!inner(target_id)")
                    .Where(cv => cv.Comments.TargetId == id)
                    .Get();

                var votes = response.Models.Select(cv => new CommentVoteResponse
                {
                    CommentId = cv.CommentId,
                    Value = cv.Value
                }).ToList();

                return Ok(ApiResponse<List<CommentVoteResponse>>.Success(votes));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve user comment votes", ex.Message));
            }
        }

        /// <summary>
        /// Get replies for a specific comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A list of replies for the comment.</returns>
        [HttpGet("{commentId}/replies")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(ApiResponse<List<CommentWithRepliesResponse>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> GetCommentReplies(Guid commentId)
        {
            try
            {
                await _supabaseService.InitializeAsync();
                var response = await _supabaseService.Client.Rpc("get_comment_replies_recursive", new { p_comment_id = commentId });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(ApiResponse<List<CommentWithRepliesResponse>>.Success(new List<CommentWithRepliesResponse>()));
                }

                var allReplies = JsonSerializer.Deserialize<List<CommentWithRepliesResponse>>(response.Content, _jsonOptions) ?? new List<CommentWithRepliesResponse>();

                var replies = BuildReplyTree(allReplies, commentId);
                return Ok(ApiResponse<List<CommentWithRepliesResponse>>.Success(replies));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve comment replies", ex.Message));
            }
        }

        private static List<CommentWithRepliesResponse> BuildReplyTree(List<CommentWithRepliesResponse> allReplies, Guid parentId)
        {
            var directReplies = allReplies.Where(r => r.ParentId == parentId).ToList();

            foreach (var reply in directReplies)
            {
                reply.Replies = BuildReplyTree(allReplies, reply.Id);
            }

            return directReplies.OrderBy(r => r.CreatedAt).ToList();
        }

        /// <summary>
        /// Vote on a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <param name="request">The vote request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{commentId}/vote")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> VoteComment(Guid commentId, [FromBody] VoteCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.Deleted)
                    return BadRequest(ApiResponse<ErrorData>.Error("Cannot vote on deleted comment"));

                // Check if user already voted
                var existingVote = await _supabaseService.Client
                    .From<CommentVoteDto>()
                    .Where(v => v.CommentId == commentId && v.UserId == userId)
                    .Single();

                if (existingVote != null)
                {
                    existingVote.Value = request.Value;
                    await _supabaseService.Client.From<CommentVoteDto>().Update(existingVote);
                }
                else
                {
                    var vote = new CommentVoteDto
                    {
                        CommentId = commentId,
                        UserId = userId,
                        Value = request.Value
                    };

                    await _supabaseService.Client.From<CommentVoteDto>().Insert(vote);
                }

                return Ok(ApiResponse<string>.Success("Vote recorded successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to vote on comment", ex.Message));
            }
        }

        /// <summary>
        /// Create a comment on a target
        /// </summary>
        /// <param name="id">The target ID.</param>
        /// <param name="request">The comment creation request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{id}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<CommentResponse>), 201)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> CreateComment(Guid id, [FromBody] CreateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                if (request.ParentId.HasValue)
                {
                    var parentComment = await _supabaseService.Client
                        .From<CommentDto>()
                        .Where(c => c.Id == request.ParentId.Value && c.TargetId == id)
                        .Single();

                    if (parentComment == null)
                        return BadRequest(ApiResponse<ErrorData>.Error("Invalid parent comment"));
                }

                var comment = new CommentDto
                {
                    TargetType = request.TargetType,
                    TargetId = id,
                    UserId = userId,
                    ParentId = request.ParentId,
                    Content = request.Content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Edited = false,
                    Deleted = false,
                    Upvotes = 0,
                    Downvotes = 0,
                    AttachmentId = request.AttachmentId
                };

                var response = await _supabaseService.Client.From<CommentDto>().Insert(comment);

                var createdComment = response.Models.FirstOrDefault();
                if (createdComment == null)
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to create comment"));

                var userProfile = await _supabaseService.Client.From<ProfileDto>().Where(p => p.Id == userId).Single();

                if (userProfile == null)
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to retrieve user profile"));

                UploadResponse? attachment = null;
                if (createdComment.AttachmentId.HasValue)
                {
                    var upload = await _supabaseService.Client.From<UploadDto>().Where(u => u.Id == createdComment.AttachmentId.Value).Single();
                    if (upload != null)
                    {
                        attachment = new UploadResponse
                        {
                            Id = upload.Id,
                            UserId = upload.UserId,
                            Md5Hash = upload.Md5Hash,
                            Size = upload.Size,
                            Url = upload.Url,
                            UsageCount = upload.UsageCount,
                            Tags = upload.Tags,
                            CreatedAt = upload.CreatedAt
                        };
                    }
                }

                var commentResponse = new CommentResponse
                {
                    Id = createdComment.Id,
                    TargetType = createdComment.TargetType,
                    TargetId = createdComment.TargetId,
                    UserProfile = new UserProfile
                    {
                        Id = userProfile.Id,
                        Username = userProfile.Username,
                        DisplayName = userProfile.DisplayName
                    },
                    ParentId = createdComment.ParentId,
                    Content = createdComment.Content,
                    CreatedAt = createdComment.CreatedAt,
                    UpdatedAt = createdComment.UpdatedAt,
                    Edited = createdComment.Edited,
                    Deleted = createdComment.Deleted,
                    Upvotes = createdComment.Upvotes,
                    Downvotes = createdComment.Downvotes,
                    ReplyCount = 0,
                    Attachment = attachment
                };

                return Created("", ApiResponse<CommentResponse>.Success(commentResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to create comment", ex.Message));
            }
        }

        /// <summary>
        /// Update a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <param name="request">The update request.</param>
        /// <returns>A success message.</returns>
        [HttpPut("{commentId}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 403)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> UpdateComment(Guid commentId, [FromBody] UpdateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ApiResponse<ErrorData>.Error("Forbidden", "You can only edit your own comments"));

                if (comment.Deleted)
                    return BadRequest(ApiResponse<ErrorData>.Error("Cannot edit deleted comment"));

                comment.Content = request.Content;
                comment.UpdatedAt = DateTimeOffset.UtcNow;
                comment.Edited = true;

                await _supabaseService.Client.From<CommentDto>().Update(comment);

                return Ok(ApiResponse<string>.Success("Comment updated successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to update comment", ex.Message));
            }
        }

        /// <summary>
        /// Delete a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A success message.</returns>
        [HttpDelete("{commentId}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 403)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 404)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> DeleteComment(Guid commentId)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ApiResponse<ErrorData>.Error("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ApiResponse<ErrorData>.Error("Forbidden", "You can only delete your own comments"));

                // Soft delete
                comment.Deleted = true;
                comment.Content = "[deleted]";
                comment.UpdatedAt = DateTimeOffset.UtcNow;

                await _supabaseService.Client.From<CommentDto>().Update(comment);

                return Ok(ApiResponse<string>.Success("Comment deleted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to delete comment", ex.Message));
            }
        }
    }
}