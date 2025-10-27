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
        [ProducesResponseType(typeof(SuccessResponse<PaginatedCommentResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
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

                if (totalCount == 0)
                {
                    return NotFound(ErrorResponse.Create("No comments found for the specified target.", status: 404));
                }

                var response = await _supabaseService.Client
                    .Rpc("get_comments_for_target", new { target_id = id, page_number = clampedPage, page_size = clampedPageSize });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<PaginatedCommentResponse>.Create(new PaginatedCommentResponse
                    {
                        Items = new List<CommentResponse>(),
                        TotalItems = totalCount,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var commentsData = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(response.Content, _jsonOptions) ?? new List<Dictionary<string, JsonElement>>();

                var comments = commentsData.Select(c => new CommentResponse
                {
                    Id = c["id"].GetGuid(),
                    TargetType = c["target_type"].GetString() ?? "",
                    TargetId = c["target_id"].GetGuid(),
                    UserProfile = new UserProfile
                    {
                        Id = c["user_id"].GetGuid(),
                        Username = c["username"].GetString() ?? "",
                        DisplayName = c["display_name"].GetString() ?? "",
                    },
                    ParentId = c["parent_id"].ValueKind != JsonValueKind.Null ? c["parent_id"].GetGuid() : (Guid?)null,
                    Content = c["content"].GetString() ?? "",
                    CreatedAt = c["created_at"].GetDateTimeOffset(),
                    UpdatedAt = c["updated_at"].GetDateTimeOffset(),
                    Edited = c["edited"].GetBoolean(),
                    Deleted = c["deleted"].GetBoolean(),
                    Upvotes = c["upvotes"].GetInt32(),
                    Downvotes = c["downvotes"].GetInt32(),
                    ReplyCount = c["reply_count"].GetInt64(),
                    Attachment = c["upload_id"].ValueKind != JsonValueKind.Null ? new UploadResponse
                    {
                        Id = c["upload_id"].GetGuid(),
                        UserId = c["upload_user_id"].GetGuid().ToString(),
                        Md5Hash = c["upload_md5_hash"].GetString() ?? "",
                        Size = c["upload_size"].GetInt64(),
                        Url = c["upload_url"].GetString() ?? "",
                        UsageCount = c["upload_usage_count"].GetInt32(),
                        Tags = c["upload_tags"].EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
                        CreatedAt = c["upload_created_at"].GetDateTimeOffset().DateTime
                    } : null,
                }).ToList();

                return Ok(SuccessResponse<PaginatedCommentResponse>.Create(new PaginatedCommentResponse
                {
                    Items = comments,
                    TotalItems = totalCount,
                    CurrentPage = clampedPage,
                    PageSize = clampedPageSize
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("An error occurred while fetching comments.", ex.Message));
            }
        }

        /// <summary>
        /// Get user's votes on comments in a target
        /// </summary>
        /// <param name="id">The comment target ID.</param>
        /// <returns>A list of the user's votes on comments in the target.</returns>
        [HttpGet("{id}/votes")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<List<CommentVoteResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUserCommentVotes(Guid id)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var response = await _supabaseService.Client
                    .Rpc("get_user_comment_votes_by_target", new { user_id = userId, target_id = id });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<List<CommentVoteResponse>>.Create(new List<CommentVoteResponse>()));
                }

                var votes = JsonSerializer.Deserialize<List<CommentVoteResponse>>(response.Content, _jsonOptions) ?? new List<CommentVoteResponse>();

                return Ok(SuccessResponse<List<CommentVoteResponse>>.Create(votes));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user comment votes", ex.Message));
            }
        }

        /// <summary>
        /// Get replies for a specific comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A list of replies for the comment.</returns>
        [HttpGet("{commentId}/replies")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<List<CommentWithRepliesResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetCommentReplies(Guid commentId)
        {
            try
            {
                await _supabaseService.InitializeAsync();
                var response = await _supabaseService.Client.Rpc("get_comment_replies_recursive", new { p_comment_id = commentId });

                if (string.IsNullOrEmpty(response.Content))
                {
                    return Ok(SuccessResponse<List<CommentWithRepliesResponse>>.Create(new List<CommentWithRepliesResponse>()));
                }

                var allReplies = JsonSerializer.Deserialize<List<CommentWithRepliesResponse>>(response.Content, _jsonOptions) ?? new List<CommentWithRepliesResponse>();

                var replies = BuildReplyTree(allReplies, commentId);
                return Ok(SuccessResponse<List<CommentWithRepliesResponse>>.Create(replies));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve comment replies", ex.Message));
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
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> VoteComment(Guid commentId, [FromBody] VoteCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                if (comment.Deleted)
                    return BadRequest(ErrorResponse.Create("Cannot vote on deleted comment"));

                // Check if user already voted
                var existingVote = await _supabaseService.Client
                    .From<CommentVoteDto>()
                    .Where(v => v.CommentId == commentId && v.UserId == userId)
                    .Single();

                if (request.Value == 0)
                {
                    // Remove the vote if it exists
                    if (existingVote != null)
                    {
                        await _supabaseService.Client.From<CommentVoteDto>().Delete(existingVote);
                    }
                    return Ok(SuccessResponse<string>.Create("Vote removed successfully"));
                }
                else
                {
                    // Insert or update the vote
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
                    return Ok(SuccessResponse<string>.Create("Vote recorded successfully"));
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to vote on comment", ex.Message));
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
        [ProducesResponseType(typeof(SuccessResponse<CommentResponse>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> CreateComment(Guid id, [FromBody] CreateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
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
                        return BadRequest(ErrorResponse.Create("Invalid parent comment"));
                    if (parentComment.Deleted)
                        return BadRequest(ErrorResponse.Create("Cannot reply to a deleted comment"));
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
                    return StatusCode(500, ErrorResponse.Create("Failed to create comment"));

                var userProfile = await _supabaseService.Client.From<ProfileDto>().Where(p => p.Id == userId).Single();

                if (userProfile == null)
                    return StatusCode(500, ErrorResponse.Create("Failed to retrieve user profile"));

                UploadResponse? attachment = null;
                if (createdComment.AttachmentId.HasValue)
                {
                    var upload = await _supabaseService.Client.From<UploadDto>().Where(u => u.Id == createdComment.AttachmentId.Value).Single();
                    if (upload != null)
                    {
                        attachment = new UploadResponse
                        {
                            Id = upload.Id,
                            UserId = upload.UserId ?? "",
                            Md5Hash = upload.Md5Hash ?? "",
                            Size = upload.Size,
                            Url = upload.Url ?? "",
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

                return Created("", SuccessResponse<CommentResponse>.Create(commentResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to create comment", ex.Message));
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
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateComment(Guid commentId, [FromBody] UpdateCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only edit your own comments"));

                if (comment.Deleted)
                    return BadRequest(ErrorResponse.Create("Cannot edit deleted comment"));

                comment.Content = request.Content;
                comment.UpdatedAt = DateTimeOffset.UtcNow;
                comment.Edited = true;

                await _supabaseService.Client.From<CommentDto>().Update(comment);

                return Ok(SuccessResponse<string>.Create("Comment updated successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to update comment", ex.Message));
            }
        }

        /// <summary>
        /// Delete a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <returns>A success message.</returns>
        [HttpDelete("{commentId}")]
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> DeleteComment(Guid commentId)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var comment = await _supabaseService.Client
                    .From<CommentDto>()
                    .Where(c => c.Id == commentId)
                    .Single();

                if (comment == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                if (comment.UserId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only delete your own comments"));

                // Soft delete
                comment.Deleted = true;
                comment.Content = "[deleted]";
                comment.UpdatedAt = DateTimeOffset.UtcNow;

                await _supabaseService.Client.From<CommentDto>().Update(comment);

                return Ok(SuccessResponse<string>.Create("Comment deleted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to delete comment", ex.Message));
            }
        }
    }
}