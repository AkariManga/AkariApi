using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Npgsql;
using Dapper;

namespace AkariApi.Controllers
{
    public enum CommentSortOrder
    {
        Latest,
        Upvoted
    }

    [ApiController]
    [Route("v2/comments")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class CommentsController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public CommentsController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        private static CommentResponse MapCommentRow(dynamic r)
        {
            return new CommentResponse
            {
                Id = (Guid)r.id,
                TargetType = (string)r.target_type,
                TargetId = (Guid)r.target_id,
                UserProfile = new UserProfile
                {
                    Id = (Guid)r.user_id,
                    Username = (string)r.username,
                    DisplayName = (string)r.display_name,
                    Role = (string)r.role,
                    Banned = (bool)r.banned,
                },
                ParentId = r.parent_id == null ? (Guid?)null : (Guid)r.parent_id,
                Content = (string)r.content,
                CreatedAt = (DateTime)r.created_at,
                UpdatedAt = (DateTime)r.updated_at,
                Edited = (bool)r.edited,
                Deleted = (bool)r.deleted,
                Upvotes = (int)r.upvotes,
                Downvotes = (int)r.downvotes,
                ReplyCount = r.reply_count != null ? (long)r.reply_count : 0L,
                Attachment = r.attachment_id == null ? null : new UploadResponse
                {
                    Id = (Guid)r.attachment_id,
                    UserId = (Guid)r.user_id,
                    Md5Hash = r.upload_md5_hash == null ? null : (string)r.upload_md5_hash,
                    Size = (long)r.upload_size,
                    Url = r.upload_url == null ? null : (string)r.upload_url,
                    UsageCount = (int)r.upload_usage_count,
                    Tags = (string[])r.upload_tags,
                    CreatedAt = (DateTime)r.upload_created_at,
                    Deleted = (bool)r.upload_deleted
                }
            };
        }

        private static CommentWithRepliesResponse MapCommentWithRepliesRow(dynamic r)
        {
            return new CommentWithRepliesResponse
            {
                Id = (Guid)r.id,
                TargetType = (string)r.target_type,
                TargetId = (Guid)r.target_id,
                UserProfile = new UserProfile
                {
                    Id = (Guid)r.user_id,
                    Username = (string)r.username,
                    DisplayName = (string)r.display_name,
                    Role = (string)r.role,
                    Banned = (bool)r.banned,
                },
                ParentId = r.parent_id == null ? (Guid?)null : (Guid)r.parent_id,
                Content = (string)r.content,
                CreatedAt = (DateTime)r.created_at,
                UpdatedAt = (DateTime)r.updated_at,
                Edited = (bool)r.edited,
                Deleted = (bool)r.deleted,
                Upvotes = (int)r.upvotes,
                Downvotes = (int)r.downvotes,
                Attachment = r.attachment_id == null ? null : new UploadResponse
                {
                    Id = (Guid)r.attachment_id,
                    UserId = (Guid)r.user_id,
                    Md5Hash = r.upload_md5_hash == null ? null : (string)r.upload_md5_hash,
                    Size = (long)r.upload_size,
                    Url = r.upload_url == null ? null : (string)r.upload_url,
                    UsageCount = (int)r.upload_usage_count,
                    Tags = (string[])r.upload_tags,
                    CreatedAt = (DateTime)r.upload_created_at,
                    Deleted = (bool)r.upload_deleted
                },
                Replies = new List<CommentWithRepliesResponse>()
            };
        }

        /// <summary>
        /// Get comments for a target
        /// </summary>
        /// <param name="id">The comment target ID.</param>
        /// <param name="page">The page number.</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <param name="sort">The sort order: Latest or Upvoted. Defaults to Latest.</param>
        /// <returns>A paginated list of top-level comments for the target, sorted by the specified order.</returns>
        [HttpGet("{id}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<PaginatedCommentResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetComments(Guid id, int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20, [FromQuery] CommentSortOrder sort = CommentSortOrder.Latest)
        {
            var (clampedPage, clampedPageSize) = PaginationHelper.ClampPagination(page, pageSize);
            var offset = (clampedPage - 1) * clampedPageSize;

            var orderBy = sort == CommentSortOrder.Upvoted ? "(c.upvotes - c.downvotes) DESC" : "c.created_at DESC";

            try
            {
                await _postgresService.OpenAsync();

                var countQuery = @"
                    SELECT COUNT(*)
                    FROM comments
                    WHERE parent_id IS NULL AND target_id = @target_id";

                long totalCount = await _postgresService.Connection.ExecuteScalarAsync<long>(countQuery, new { target_id = id });

                if (totalCount == 0)
                {
                    return Ok(SuccessResponse<PaginatedCommentResponse>.Create(new PaginatedCommentResponse
                    {
                        Items = [],
                        TotalItems = (int)totalCount,
                        CurrentPage = clampedPage,
                        PageSize = clampedPageSize
                    }));
                }

                var commentsQuery = $@"
                    SELECT
                        c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                        c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                        c.attachment_id,
                        u.username, u.display_name, u.role, u.banned,
                        up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                        up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at,
                        up.deleted as upload_deleted,
                        COALESCE(reply_counts.reply_count, 0) as reply_count
                    FROM comments c
                    JOIN profiles u ON c.user_id = u.id
                    LEFT JOIN uploads up ON c.attachment_id = up.id
                    LEFT JOIN (
                        SELECT parent_id, COUNT(*) as reply_count
                        FROM comments
                        GROUP BY parent_id
                    ) reply_counts ON c.id = reply_counts.parent_id
                    WHERE c.parent_id IS NULL AND c.target_id = @target_id
                    ORDER BY {orderBy}
                    LIMIT @limit OFFSET @offset";

                var rows = await _postgresService.Connection.QueryAsync(commentsQuery, new { target_id = id, limit = clampedPageSize, offset });
                var comments = rows.Select(r => MapCommentRow(r)).Cast<CommentResponse>().ToList();

                return Ok(SuccessResponse<PaginatedCommentResponse>.Create(new PaginatedCommentResponse
                {
                    Items = comments,
                    TotalItems = (int)totalCount,
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
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<List<CommentVoteResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUserCommentVotes(Guid id)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    SELECT cv.comment_id, cv.value, c.target_id
                    FROM comment_votes cv
                    JOIN comments c ON cv.comment_id = c.id
                    WHERE cv.user_id = @user_id AND c.target_id = @target_id";

                var rows = await _postgresService.Connection.QueryAsync(query, new { user_id = userId, target_id = id });
                var votes = rows.Select(r => new CommentVoteResponse
                {
                    CommentId = (Guid)r.comment_id,
                    Value = (short)r.value,
                    TargetId = (Guid)r.target_id
                }).ToList();

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
                await _postgresService.OpenAsync();

                var query = @"
                    WITH RECURSIVE comment_tree AS (
                        SELECT
                            c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                            c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                            c.attachment_id,
                            u.username, u.display_name, u.role, u.banned,
                            up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                            up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at,
                            up.deleted as upload_deleted
                        FROM comments c
                        JOIN profiles u ON c.user_id = u.id
                        LEFT JOIN uploads up ON c.attachment_id = up.id
                        WHERE c.id = @comment_id

                        UNION ALL

                        SELECT
                            c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                            c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                            c.attachment_id,
                            u.username, u.display_name, u.role, u.banned,
                            up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                            up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at,
                            up.deleted as upload_deleted
                        FROM comments c
                        JOIN profiles u ON c.user_id = u.id
                        LEFT JOIN uploads up ON c.attachment_id = up.id
                        INNER JOIN comment_tree ct ON c.parent_id = ct.id
                    )
                    SELECT * FROM comment_tree WHERE id != @comment_id ORDER BY created_at ASC";

                var rows = await _postgresService.Connection.QueryAsync(query, new { comment_id = commentId });
                var allReplies = rows.Select(r => MapCommentWithRepliesRow(r)).Cast<CommentWithRepliesResponse>().ToList();

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
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> VoteComment(Guid commentId, [FromBody] VoteCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var commentQuery = "SELECT deleted FROM comments WHERE id = @comment_id";
                var isDeletedResult = await _postgresService.Connection.ExecuteScalarAsync<bool?>(commentQuery, new { comment_id = commentId });
                if (isDeletedResult == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));
                bool isDeleted = isDeletedResult.Value;

                if (isDeleted)
                    return BadRequest(ErrorResponse.Create("Cannot vote on deleted comment"));

                var existingVoteQuery = "SELECT value FROM comment_votes WHERE comment_id = @comment_id AND user_id = @user_id";
                short? existingVoteValue = await _postgresService.Connection.ExecuteScalarAsync<short?>(existingVoteQuery, new { comment_id = commentId, user_id = userId });

                if (request.Value == 0)
                {
                    if (existingVoteValue.HasValue)
                    {
                        await _postgresService.Connection.ExecuteAsync(
                            "DELETE FROM comment_votes WHERE comment_id = @comment_id AND user_id = @user_id",
                            new { comment_id = commentId, user_id = userId });
                    }
                    return Ok(SuccessResponse<string>.Create("Vote removed successfully"));
                }
                else
                {
                    if (existingVoteValue.HasValue)
                    {
                        await _postgresService.Connection.ExecuteAsync(
                            "UPDATE comment_votes SET value = @value WHERE comment_id = @comment_id AND user_id = @user_id",
                            new { comment_id = commentId, user_id = userId, value = request.Value });
                    }
                    else
                    {
                        await _postgresService.Connection.ExecuteAsync(
                            "INSERT INTO comment_votes (comment_id, user_id, value) VALUES (@comment_id, @user_id, @value)",
                            new { comment_id = commentId, user_id = userId, value = request.Value });
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
        /// Report a comment
        /// </summary>
        /// <param name="commentId">The unique identifier of the comment.</param>
        /// <param name="request">The report request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{commentId}/report")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 409)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> ReportComment(Guid commentId, [FromBody] ReportCommentRequest request)
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var commentQuery = "SELECT user_id FROM comments WHERE id = @comment_id";
                var commentOwnerId = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(commentQuery, new { comment_id = commentId });
                if (commentOwnerId == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                if (commentOwnerId == userId)
                {
                    return BadRequest(ErrorResponse.Create("You cannot report your own comment"));
                }

                var reasonString = request.Reason switch
                {
                    CommentReportReason.spam => "spam",
                    CommentReportReason.harassment => "harassment",
                    CommentReportReason.inappropriate => "inappropriate",
                    CommentReportReason.hate_speech => "hate-speech",
                    CommentReportReason.other => "other",
                    _ => throw new ArgumentException("Invalid reason")
                };

                var insertQuery = @"
                    INSERT INTO comment_reports (comment_id, user_id, reason, description)
                    VALUES (@comment_id, @user_id, @reason, @description)";

                await _postgresService.Connection.ExecuteAsync(insertQuery, new
                {
                    comment_id = commentId,
                    user_id = userId,
                    reason = reasonString,
                    description = (object?)request.Description ?? DBNull.Value
                });

                return StatusCode(201, SuccessResponse<string>.Create("Comment reported successfully"));
            }
            catch (PostgresException ex) when (ex.SqlState == "23505") // Unique violation
            {
                return Conflict(ErrorResponse.Create("You have already reported this comment", status: 409));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to report comment", ex.Message));
            }
        }

        /// <summary>
        /// Create a comment on a target
        /// </summary>
        /// <param name="id">The target ID.</param>
        /// <param name="request">The comment creation request.</param>
        /// <returns>A success message.</returns>
        [HttpPost("{id}")]
        [ProducesResponseType(typeof(SuccessResponse<CommentResponse>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> CreateComment(Guid id, [FromBody] CreateCommentRequest request)
        {
            if (CommentHelper.ContainsBannedContent(request.Content))
            {
                return BadRequest(ErrorResponse.Create("Comment contains banned content"));
            }

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                if (request.ParentId.HasValue)
                {
                    var parentQuery = "SELECT deleted FROM comments WHERE id = @parent_id AND target_id = @target_id";
                    var parentDeleted = await _postgresService.Connection.ExecuteScalarAsync<bool?>(parentQuery, new { parent_id = request.ParentId.Value, target_id = id });
                    if (parentDeleted == null)
                        return BadRequest(ErrorResponse.Create("Invalid parent comment"));
                    if (parentDeleted.Value)
                        return BadRequest(ErrorResponse.Create("Cannot reply to a deleted comment"));
                }

                var insertQuery = @"
                    INSERT INTO comments (target_type, target_id, user_id, parent_id, content, created_at, updated_at, edited, deleted, upvotes, downvotes, attachment_id)
                    VALUES (@target_type, @target_id, @user_id, @parent_id, @content, @created_at, @updated_at, @edited, @deleted, @upvotes, @downvotes, @attachment_id)
                    RETURNING id";

                var commentId = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(insertQuery, new
                {
                    target_type = request.TargetType,
                    target_id = id,
                    user_id = userId,
                    parent_id = request.ParentId.HasValue ? (object)request.ParentId.Value : DBNull.Value,
                    content = request.Content,
                    created_at = DateTimeOffset.UtcNow,
                    updated_at = DateTimeOffset.UtcNow,
                    edited = false,
                    deleted = false,
                    upvotes = 0,
                    downvotes = 0,
                    attachment_id = request.AttachmentId.HasValue ? (object)request.AttachmentId.Value : DBNull.Value
                });

                if (commentId == null)
                    return StatusCode(500, ErrorResponse.Create("Failed to create comment"));

                var fetchQuery = @"
                    SELECT
                        c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                        c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                        c.attachment_id,
                        u.username, u.display_name, u.role, u.banned,
                        up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                        up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at,
                        up.deleted as upload_deleted
                    FROM comments c
                    JOIN profiles u ON c.user_id = u.id
                    LEFT JOIN uploads up ON c.attachment_id = up.id
                    WHERE c.id = @comment_id";

                var row = await _postgresService.Connection.QueryFirstOrDefaultAsync(fetchQuery, new { comment_id = commentId.Value });
                if (row == null)
                    return StatusCode(500, ErrorResponse.Create("Failed to retrieve created comment"));

                var commentResponse = MapCommentRow(row);
                commentResponse.ReplyCount = 0;

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
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 403)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateComment(Guid commentId, [FromBody] UpdateCommentRequest request)
        {
            if (CommentHelper.ContainsBannedContent(request.Content))
            {
                return BadRequest(ErrorResponse.Create("Comment contains banned content"));
            }

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var checkQuery = "SELECT user_id, deleted FROM comments WHERE id = @comment_id";
                var checkRow = await _postgresService.Connection.QueryFirstOrDefaultAsync(checkQuery, new { comment_id = commentId });
                if (checkRow == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                Guid ownerId = (Guid)checkRow.user_id;
                bool isDeleted = (bool)checkRow.deleted;

                if (ownerId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only edit your own comments"));

                if (isDeleted)
                    return BadRequest(ErrorResponse.Create("Cannot edit deleted comment"));

                await _postgresService.Connection.ExecuteAsync(
                    "UPDATE comments SET content = @content, updated_at = @updated_at, edited = true WHERE id = @comment_id",
                    new { comment_id = commentId, content = request.Content, updated_at = DateTimeOffset.UtcNow });

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

            var isBanned = await AuthenticationHelper.IsUserBannedAsync(userId, _postgresService);
            if (isBanned)
            {
                return StatusCode(403, ErrorResponse.Create("Forbidden", "Your account is banned"));
            }

            try
            {
                await _postgresService.OpenAsync();

                var ownerId = await _postgresService.Connection.ExecuteScalarAsync<Guid?>(
                    "SELECT user_id FROM comments WHERE id = @comment_id",
                    new { comment_id = commentId });
                if (ownerId == null)
                    return NotFound(ErrorResponse.Create("Comment not found", status: 404));

                if (ownerId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only delete your own comments"));

                long replyCount = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM comments WHERE parent_id = @comment_id",
                    new { comment_id = commentId });

                if (replyCount > 0)
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "UPDATE comments SET deleted = true, content = '[deleted]', updated_at = @updated_at WHERE id = @comment_id",
                        new { comment_id = commentId, updated_at = DateTimeOffset.UtcNow });
                }
                else
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "DELETE FROM comments WHERE id = @comment_id",
                        new { comment_id = commentId });
                }

                return Ok(SuccessResponse<string>.Create("Comment deleted successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to delete comment", ex.Message));
            }
        }
    }
}
