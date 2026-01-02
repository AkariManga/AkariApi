using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using AkariApi.Helpers;
using Npgsql;

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

                // Get total count
                var countQuery = @"
                    SELECT COUNT(*)
                    FROM comments
                    WHERE parent_id IS NULL AND target_id = @target_id";

                long totalCount;
                using (var cmd = new NpgsqlCommand(countQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@target_id", id);
                    var result = await cmd.ExecuteScalarAsync();
                    totalCount = result != null ? (long)result : 0;
                }

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

                // Get paginated comments with user and attachment info
                var commentsQuery = $@"
                    SELECT
                        c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                        c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                        c.attachment_id,
                        u.username, u.display_name, u.role,
                        up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                        up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at,
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

                var comments = new List<CommentResponse>();
                using (var cmd = new NpgsqlCommand(commentsQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@target_id", id);
                    cmd.Parameters.AddWithValue("@limit", clampedPageSize);
                    cmd.Parameters.AddWithValue("@offset", offset);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var comment = new CommentResponse
                            {
                                Id = reader.GetGuid(reader.GetOrdinal("id")),
                                TargetType = reader.GetString(reader.GetOrdinal("target_type")),
                                TargetId = reader.GetGuid(reader.GetOrdinal("target_id")),
                                UserProfile = new UserProfile
                                {
                                    Id = reader.GetGuid(reader.GetOrdinal("user_id")),
                                    Username = reader.GetString(reader.GetOrdinal("username")),
                                    DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                                    Role = reader.GetString(reader.GetOrdinal("role")),
                                },
                                ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id")) ? (Guid?)null : reader.GetGuid(reader.GetOrdinal("parent_id")),
                                Content = reader.GetString(reader.GetOrdinal("content")),
                                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at")),
                                Edited = reader.GetBoolean(reader.GetOrdinal("edited")),
                                Deleted = reader.GetBoolean(reader.GetOrdinal("deleted")),
                                Upvotes = reader.GetInt32(reader.GetOrdinal("upvotes")),
                                Downvotes = reader.GetInt32(reader.GetOrdinal("downvotes")),
                                ReplyCount = reader.GetInt64(reader.GetOrdinal("reply_count")),
                                Attachment = reader.IsDBNull(reader.GetOrdinal("attachment_id")) ? null : new UploadResponse
                                {
                                    Id = reader.GetGuid(reader.GetOrdinal("attachment_id")),
                                    UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
                                    Md5Hash = reader.GetString(reader.GetOrdinal("upload_md5_hash")),
                                    Size = reader.GetInt64(reader.GetOrdinal("upload_size")),
                                    Url = reader.GetString(reader.GetOrdinal("upload_url")),
                                    UsageCount = reader.GetInt32(reader.GetOrdinal("upload_usage_count")),
                                    Tags = reader.GetFieldValue<string[]>(reader.GetOrdinal("upload_tags")),
                                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("upload_created_at"))
                                }
                            };
                            comments.Add(comment);
                        }
                    }
                }

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
                await _postgresService.OpenAsync();

                var query = @"
                    SELECT cv.comment_id, cv.value, c.target_id
                    FROM comment_votes cv
                    JOIN comments c ON cv.comment_id = c.id
                    WHERE cv.user_id = @user_id AND c.target_id = @target_id";

                var votes = new List<CommentVoteResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@target_id", id);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            votes.Add(new CommentVoteResponse
                            {
                                CommentId = reader.GetGuid(reader.GetOrdinal("comment_id")),
                                Value = reader.GetInt16(reader.GetOrdinal("value")),
                                TargetId = reader.GetGuid(reader.GetOrdinal("target_id"))
                            });
                        }
                    }
                }

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
                            u.username, u.display_name, u.role,
                            up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                            up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at
                        FROM comments c
                        JOIN profiles u ON c.user_id = u.id
                        LEFT JOIN uploads up ON c.attachment_id = up.id
                        WHERE c.id = @comment_id

                        UNION ALL

                        SELECT
                            c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                            c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                            c.attachment_id,
                            u.username, u.display_name, u.role,
                            up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                            up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at
                        FROM comments c
                        JOIN profiles u ON c.user_id = u.id
                        LEFT JOIN uploads up ON c.attachment_id = up.id
                        INNER JOIN comment_tree ct ON c.parent_id = ct.id
                    )
                    SELECT * FROM comment_tree WHERE id != @comment_id ORDER BY created_at ASC";

                var allReplies = new List<CommentWithRepliesResponse>();
                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var reply = new CommentWithRepliesResponse
                            {
                                Id = reader.GetGuid(reader.GetOrdinal("id")),
                                TargetType = reader.GetString(reader.GetOrdinal("target_type")),
                                TargetId = reader.GetGuid(reader.GetOrdinal("target_id")),
                                UserProfile = new UserProfile
                                {
                                    Id = reader.GetGuid(reader.GetOrdinal("user_id")),
                                    Username = reader.GetString(reader.GetOrdinal("username")),
                                    DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                                    Role = reader.GetString(reader.GetOrdinal("role")),
                                },
                                ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id")) ? (Guid?)null : reader.GetGuid(reader.GetOrdinal("parent_id")),
                                Content = reader.GetString(reader.GetOrdinal("content")),
                                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at")),
                                Edited = reader.GetBoolean(reader.GetOrdinal("edited")),
                                Deleted = reader.GetBoolean(reader.GetOrdinal("deleted")),
                                Upvotes = reader.GetInt32(reader.GetOrdinal("upvotes")),
                                Downvotes = reader.GetInt32(reader.GetOrdinal("downvotes")),
                                Attachment = reader.IsDBNull(reader.GetOrdinal("attachment_id")) ? null : new UploadResponse
                                {
                                    Id = reader.GetGuid(reader.GetOrdinal("attachment_id")),
                                    UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
                                    Md5Hash = reader.GetString(reader.GetOrdinal("upload_md5_hash")),
                                    Size = reader.GetInt64(reader.GetOrdinal("upload_size")),
                                    Url = reader.GetString(reader.GetOrdinal("upload_url")),
                                    UsageCount = reader.GetInt32(reader.GetOrdinal("upload_usage_count")),
                                    Tags = reader.GetFieldValue<string[]>(reader.GetOrdinal("upload_tags")),
                                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("upload_created_at"))
                                },
                                Replies = new List<CommentWithRepliesResponse>()
                            };
                            allReplies.Add(reply);
                        }
                    }
                }

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
                await _postgresService.OpenAsync();

                // Check if comment exists and is not deleted
                var commentQuery = "SELECT deleted FROM comments WHERE id = @comment_id";
                bool isDeleted;
                using (var cmd = new NpgsqlCommand(commentQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                        return NotFound(ErrorResponse.Create("Comment not found", status: 404));
                    isDeleted = (bool)result;
                }

                if (isDeleted)
                    return BadRequest(ErrorResponse.Create("Cannot vote on deleted comment"));

                // Check if user already voted
                var existingVoteQuery = "SELECT value FROM comment_votes WHERE comment_id = @comment_id AND user_id = @user_id";
                short? existingVoteValue = null;
                using (var cmd = new NpgsqlCommand(existingVoteQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null)
                        existingVoteValue = (short)result;
                }

                if (request.Value == 0)
                {
                    // Remove the vote if it exists
                    if (existingVoteValue.HasValue)
                    {
                        var deleteQuery = "DELETE FROM comment_votes WHERE comment_id = @comment_id AND user_id = @user_id";
                        using (var cmd = new NpgsqlCommand(deleteQuery, _postgresService.Connection))
                        {
                            cmd.Parameters.AddWithValue("@comment_id", commentId);
                            cmd.Parameters.AddWithValue("@user_id", userId);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    return Ok(SuccessResponse<string>.Create("Vote removed successfully"));
                }
                else
                {
                    // Insert or update the vote
                    if (existingVoteValue.HasValue)
                    {
                        var updateQuery = "UPDATE comment_votes SET value = @value WHERE comment_id = @comment_id AND user_id = @user_id";
                        using (var cmd = new NpgsqlCommand(updateQuery, _postgresService.Connection))
                        {
                            cmd.Parameters.AddWithValue("@comment_id", commentId);
                            cmd.Parameters.AddWithValue("@user_id", userId);
                            cmd.Parameters.AddWithValue("@value", request.Value);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    else
                    {
                        var insertQuery = "INSERT INTO comment_votes (comment_id, user_id, value) VALUES (@comment_id, @user_id, @value)";
                        using (var cmd = new NpgsqlCommand(insertQuery, _postgresService.Connection))
                        {
                            cmd.Parameters.AddWithValue("@comment_id", commentId);
                            cmd.Parameters.AddWithValue("@user_id", userId);
                            cmd.Parameters.AddWithValue("@value", request.Value);
                            await cmd.ExecuteNonQueryAsync();
                        }
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
        [RequireTokenRefresh]
        [ProducesResponseType(typeof(SuccessResponse<string>), 201)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
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

            try
            {
                await _postgresService.OpenAsync();

                // Check if comment exists
                var commentQuery = "SELECT id FROM comments WHERE id = @comment_id";
                using (var cmd = new NpgsqlCommand(commentQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                        return NotFound(ErrorResponse.Create("Comment not found", status: 404));
                }

                // Insert the report (UNIQUE constraint will prevent duplicates)
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

                using (var cmd = new NpgsqlCommand(insertQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@reason", reasonString);
                    cmd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }

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
                await _postgresService.OpenAsync();

                if (request.ParentId.HasValue)
                {
                    var parentQuery = "SELECT deleted FROM comments WHERE id = @parent_id AND target_id = @target_id";
                    using (var cmd = new NpgsqlCommand(parentQuery, _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("@parent_id", request.ParentId.Value);
                        cmd.Parameters.AddWithValue("@target_id", id);
                        var result = await cmd.ExecuteScalarAsync();
                        if (result == null)
                            return BadRequest(ErrorResponse.Create("Invalid parent comment"));
                        if ((bool)result)
                            return BadRequest(ErrorResponse.Create("Cannot reply to a deleted comment"));
                    }
                }

                var insertQuery = @"
                    INSERT INTO comments (target_type, target_id, user_id, parent_id, content, created_at, updated_at, edited, deleted, upvotes, downvotes, attachment_id)
                    VALUES (@target_type, @target_id, @user_id, @parent_id, @content, @created_at, @updated_at, @edited, @deleted, @upvotes, @downvotes, @attachment_id)
                    RETURNING id";

                Guid commentId;
                using (var cmd = new NpgsqlCommand(insertQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@target_type", request.TargetType);
                    cmd.Parameters.AddWithValue("@target_id", id);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@parent_id", request.ParentId.HasValue ? (object)request.ParentId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@content", request.Content);
                    cmd.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow);
                    cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow);
                    cmd.Parameters.AddWithValue("@edited", false);
                    cmd.Parameters.AddWithValue("@deleted", false);
                    cmd.Parameters.AddWithValue("@upvotes", 0);
                    cmd.Parameters.AddWithValue("@downvotes", 0);
                    cmd.Parameters.AddWithValue("@attachment_id", request.AttachmentId.HasValue ? (object)request.AttachmentId.Value : DBNull.Value);

                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                        return StatusCode(500, ErrorResponse.Create("Failed to create comment"));
                    commentId = (Guid)result;
                }

                // Fetch the created comment with user and attachment info
                var fetchQuery = @"
                    SELECT
                        c.id, c.target_type, c.target_id, c.user_id, c.parent_id, c.content,
                        c.created_at, c.updated_at, c.edited, c.deleted, c.upvotes, c.downvotes,
                        c.attachment_id,
                        u.username, u.display_name, u.role,
                        up.md5_hash as upload_md5_hash, up.size as upload_size, up.url as upload_url,
                        up.usage_count as upload_usage_count, up.tags as upload_tags, up.created_at as upload_created_at
                    FROM comments c
                    JOIN profiles u ON c.user_id = u.id
                    LEFT JOIN uploads up ON c.attachment_id = up.id
                    WHERE c.id = @comment_id";

                CommentResponse commentResponse;
                using (var cmd = new NpgsqlCommand(fetchQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                            return StatusCode(500, ErrorResponse.Create("Failed to retrieve created comment"));

                        commentResponse = new CommentResponse
                        {
                            Id = reader.GetGuid(reader.GetOrdinal("id")),
                            TargetType = reader.GetString(reader.GetOrdinal("target_type")),
                            TargetId = reader.GetGuid(reader.GetOrdinal("target_id")),
                            UserProfile = new UserProfile
                            {
                                Id = reader.GetGuid(reader.GetOrdinal("user_id")),
                                Username = reader.GetString(reader.GetOrdinal("username")),
                                DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
                                Role = reader.GetString(reader.GetOrdinal("role")),
                            },
                            ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id")) ? (Guid?)null : reader.GetGuid(reader.GetOrdinal("parent_id")),
                            Content = reader.GetString(reader.GetOrdinal("content")),
                            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                            UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at")),
                            Edited = reader.GetBoolean(reader.GetOrdinal("edited")),
                            Deleted = reader.GetBoolean(reader.GetOrdinal("deleted")),
                            Upvotes = reader.GetInt32(reader.GetOrdinal("upvotes")),
                            Downvotes = reader.GetInt32(reader.GetOrdinal("downvotes")),
                            ReplyCount = 0,
                            Attachment = reader.IsDBNull(reader.GetOrdinal("attachment_id")) ? null : new UploadResponse
                            {
                                Id = reader.GetGuid(reader.GetOrdinal("attachment_id")),
                                UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
                                Md5Hash = reader.GetString(reader.GetOrdinal("upload_md5_hash")),
                                Size = reader.GetInt64(reader.GetOrdinal("upload_size")),
                                Url = reader.GetString(reader.GetOrdinal("upload_url")),
                                UsageCount = reader.GetInt32(reader.GetOrdinal("upload_usage_count")),
                                Tags = reader.GetFieldValue<string[]>(reader.GetOrdinal("upload_tags")),
                                CreatedAt = reader.GetDateTime(reader.GetOrdinal("upload_created_at"))
                            }
                        };
                    }
                }

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
                await _postgresService.OpenAsync();

                // Check if comment exists and user owns it
                var checkQuery = "SELECT user_id, deleted FROM comments WHERE id = @comment_id";
                Guid? ownerId = null;
                bool? isDeleted = null;
                using (var cmd = new NpgsqlCommand(checkQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                            return NotFound(ErrorResponse.Create("Comment not found", status: 404));
                        ownerId = reader.GetGuid(reader.GetOrdinal("user_id"));
                        isDeleted = reader.GetBoolean(reader.GetOrdinal("deleted"));
                    }
                }

                if (ownerId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only edit your own comments"));

                if (isDeleted.Value)
                    return BadRequest(ErrorResponse.Create("Cannot edit deleted comment"));

                var updateQuery = "UPDATE comments SET content = @content, updated_at = @updated_at, edited = true WHERE id = @comment_id";
                using (var cmd = new NpgsqlCommand(updateQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    cmd.Parameters.AddWithValue("@content", request.Content);
                    cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow);
                    await cmd.ExecuteNonQueryAsync();
                }

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
                await _postgresService.OpenAsync();

                // Check if comment exists and user owns it
                var checkQuery = "SELECT user_id FROM comments WHERE id = @comment_id";
                Guid? ownerId = null;
                using (var cmd = new NpgsqlCommand(checkQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    var result = await cmd.ExecuteScalarAsync();
                    if (result == null)
                        return NotFound(ErrorResponse.Create("Comment not found", status: 404));
                    ownerId = (Guid)result;
                }

                if (ownerId != userId)
                    return StatusCode(403, ErrorResponse.Create("Forbidden", "You can only delete your own comments"));

                // Check if comment has replies
                var replyCountQuery = "SELECT COUNT(*) FROM comments WHERE parent_id = @comment_id";
                long replyCount;
                using (var cmd = new NpgsqlCommand(replyCountQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("@comment_id", commentId);
                    var result = await cmd.ExecuteScalarAsync();
                    replyCount = result != null ? (long)result : 0;
                }

                if (replyCount > 0)
                {
                    // Soft delete
                    var updateQuery = "UPDATE comments SET deleted = true, content = '[deleted]', updated_at = @updated_at WHERE id = @comment_id";
                    using (var cmd = new NpgsqlCommand(updateQuery, _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("@comment_id", commentId);
                        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    // Hard delete
                    var deleteQuery = "DELETE FROM comments WHERE id = @comment_id";
                    using (var cmd = new NpgsqlCommand(deleteQuery, _postgresService.Connection))
                    {
                        cmd.Parameters.AddWithValue("@comment_id", commentId);
                        await cmd.ExecuteNonQueryAsync();
                    }
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