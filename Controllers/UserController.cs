using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Helpers;
using Supabase.Gotrue;
using AkariApi.Attributes;
using Dapper;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/user")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class UserController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;

        public UserController(SupabaseService supabaseService, PostgresService postgresService)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
        }

        /// <summary>
        /// Create a user
        /// </summary>
        /// <param name="request">The signup request containing email and password.</param>
        /// <returns>The signup response.</returns>
        [HttpPost("signup")]
        [ProducesResponseType(typeof(SuccessResponse<Session>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            try
            {
                await _supabaseService.InitializeAsync();
                await _postgresService.OpenAsync();

                request.UserName = request.UserName.ToLower();

                var count = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM profiles WHERE username = @username", new { username = request.UserName });
                if (count > 0)
                {
                    await _postgresService.CloseAsync();
                    return BadRequest(ErrorResponse.Create("Invalid request", "Username already taken"));
                }

                var options = new Supabase.Gotrue.SignUpOptions
                {
                    Data = new Dictionary<string, object> { { "username", request.UserName }, { "display_name", request.DisplayName } }
                };
                var session = await _supabaseService.Client.Auth.SignUp(request.Email, request.Password, options);

                if (session == null)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign up", "User creation returned null"));
                }

                if (session.User != null && !string.IsNullOrEmpty(session.User.Id))
                {
                    await _postgresService.Connection.ExecuteAsync(
                        "INSERT INTO profiles (id, username, display_name) VALUES (@id, @username, @display_name)",
                        new { id = Guid.Parse(session.User.Id), username = request.UserName, display_name = request.DisplayName });
                }

                if (!string.IsNullOrEmpty(session.AccessToken))
                {
                    CookieHelper.SetCookie(Response, "accessToken", session.AccessToken, expires: TimeSpan.FromDays(365));
                    if (!string.IsNullOrEmpty(session.RefreshToken))
                    {
                        CookieHelper.SetCookie(Response, "refreshToken", session.RefreshToken, expires: TimeSpan.FromDays(365));
                    }
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<Session>.Create(session));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to sign up", ex.Message));
            }
        }

        /// <summary>
        /// Sign in a user
        /// </summary>
        /// <param name="request">The signin request containing email and password.</param>
        /// <returns>The signin response.</returns>
        [HttpPost("signin")]
        [ProducesResponseType(typeof(SuccessResponse<UserResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SignIn([FromBody] SignInRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            try
            {
                await _supabaseService.InitializeAsync();
                await _postgresService.OpenAsync();

                var session = await _supabaseService.Client.Auth.SignIn(request.Email, request.Password);

                if (session == null)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign in", "Authentication returned null"));
                }

                if (session.User == null || string.IsNullOrEmpty(session.User.Id))
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign in", "User ID not found in session"));
                }

                if (!Guid.TryParse(session.User.Id, out var userId))
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign in", "Invalid user ID format"));
                }

                var userResponse = await _postgresService.Connection.QueryFirstOrDefaultAsync<UserResponse>(
                    "SELECT id AS \"UserId\", username, display_name, role, banned FROM profiles WHERE id = @id",
                    new { id = userId });

                if (userResponse == null)
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign in", "Profile not found"));
                }

                if (!string.IsNullOrEmpty(session.AccessToken))
                {
                    CookieHelper.SetCookie(Response, "accessToken", session.AccessToken, expires: TimeSpan.FromDays(365));
                    if (!string.IsNullOrEmpty(session.RefreshToken))
                    {
                        CookieHelper.SetCookie(Response, "refreshToken", session.RefreshToken, expires: TimeSpan.FromDays(365));
                    }
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<UserResponse>.Create(userResponse));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to sign in", ex.Message));
            }
        }

        /// <summary>
        /// Sign out a user
        /// </summary>
        /// <returns>The signout response.</returns>
        [HttpPost("signout")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public new async Task<IActionResult> SignOut()
        {
            try
            {
                await _supabaseService.InitializeAsync();

                await _supabaseService.Client.Auth.SignOut();

                Response.Cookies.Delete("accessToken");
                Response.Cookies.Delete("refreshToken");

                return Ok(SuccessResponse<string>.Create("Signed out successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to sign out", ex.Message));
            }
        }

        /// <summary>
        /// Get current user
        /// </summary>
        /// <returns>The user information.</returns>
        [HttpGet("me")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetMe()
        {
            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _postgresService.OpenAsync();

                var userResponse = await _postgresService.Connection.QueryFirstOrDefaultAsync<UserResponse>(
                    "SELECT id AS \"UserId\", username, display_name, role, banned FROM profiles WHERE id = @id",
                    new { id = userId });

                if (userResponse == null)
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", "Profile not found"));
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<UserResponse>.Create(userResponse));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return Unauthorized(ErrorResponse.Create("Unauthorized", ex.Message));
            }
        }

        /// <summary>
        /// Get a paginated list of users
        /// </summary>
        /// <param name="page">The page number (1-based).</param>
        /// <param name="pageSize">The number of items per page.</param>
        /// <param name="sortBy">The field to sort by.</param>
        /// <param name="sortDirection">The sort direction.</param>
        /// <returns>A paginated list of user profiles.</returns>
        [HttpGet]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<PaginatedResponse<UserProfileDetailsResponse>>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUsers(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] UserSortBy sortBy = UserSortBy.CreatedAt,
            [FromQuery] SortDirection sortDirection = SortDirection.Descending)
        {
            (page, pageSize) = PaginationHelper.ClampPagination(page, pageSize);

            var sortColumn = sortBy switch
            {
                UserSortBy.Username => "p.username",
                UserSortBy.TotalComments => "total_comments",
                UserSortBy.TotalUpvotes => "total_upvotes",
                UserSortBy.TotalBookmarks => "total_bookmarks",
                UserSortBy.TotalUploads => "total_uploads",
                UserSortBy.TotalLists => "total_lists",
                _ => "u.created_at"
            };
            var sortDir = sortDirection == SortDirection.Ascending ? "ASC" : "DESC";

            try
            {
                await _postgresService.OpenAsync();

                var countSql = "SELECT COUNT(*) FROM profiles p";
                var totalItems = await _postgresService.Connection.ExecuteScalarAsync<int>(countSql);

                var query = $@"
                    SELECT
                        p.id AS ""UserId"",
                        p.username,
                        p.display_name,
                        p.role,
                        p.banned,
                        u.created_at,
                        COALESCE(c.comment_count, 0) AS total_comments,
                        COALESCE(c.total_upvotes, 0) AS total_upvotes,
                        COALESCE(c.total_downvotes, 0) AS total_downvotes,
                        COALESCE(b.bookmark_count, 0) AS total_bookmarks,
                        COALESCE(up.upload_count, 0) AS total_uploads,
                        COALESCE(l.list_count, 0) AS total_lists
                    FROM profiles p
                    LEFT JOIN auth.users u ON p.id = u.id
                    LEFT JOIN (
                        SELECT
                            user_id,
                            COUNT(*) AS comment_count,
                            SUM(upvotes) AS total_upvotes,
                            SUM(downvotes) AS total_downvotes
                        FROM comments
                        WHERE deleted = false
                        GROUP BY user_id
                    ) c ON p.id = c.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS bookmark_count
                        FROM user_bookmarks
                        GROUP BY user_id
                    ) b ON p.id = b.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS upload_count
                        FROM uploads
                        GROUP BY user_id
                    ) up ON p.id = up.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS list_count
                        FROM user_manga_lists
                        GROUP BY user_id
                    ) l ON p.id = l.user_id
                    ORDER BY {sortColumn} {sortDir}
                    LIMIT @pageSize OFFSET @offset";

                var users = (await _postgresService.Connection.QueryAsync<UserProfileDetailsResponse>(
                    query, new { pageSize, offset = (page - 1) * pageSize })).ToList();

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<PaginatedResponse<UserProfileDetailsResponse>>.Create(
                    new PaginatedResponse<UserProfileDetailsResponse>
                    {
                        Items = users,
                        TotalItems = totalItems,
                        CurrentPage = page,
                        PageSize = pageSize
                    }));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve users", ex.Message));
            }
        }

        /// <summary>
        /// Get user profile details by user ID
        /// </summary>
        /// <param name="userId">The unique identifier of the user.</param>
        /// <returns>The detailed user profile information including statistics.</returns>
        [HttpGet("{userId}")]
        [CacheControl(CacheDuration.FiveMinutes, CacheDuration.TenMinutes)]
        [ProducesResponseType(typeof(SuccessResponse<UserProfileDetailsResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 404)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> GetUserById(Guid userId)
        {
            try
            {
                await _postgresService.OpenAsync();

                var query = @"
                    SELECT
                        p.id AS ""UserId"",
                        p.username,
                        p.display_name,
                        p.role,
                        p.banned,
                        u.created_at,
                        COALESCE(c.comment_count, 0) AS total_comments,
                        COALESCE(c.total_upvotes, 0) AS total_upvotes,
                        COALESCE(c.total_downvotes, 0) AS total_downvotes,
                        COALESCE(b.bookmark_count, 0) AS total_bookmarks,
                        COALESCE(up.upload_count, 0) AS total_uploads,
                        COALESCE(l.list_count, 0) AS total_lists
                    FROM profiles p
                    LEFT JOIN auth.users u ON p.id = u.id
                    LEFT JOIN (
                        SELECT
                            user_id,
                            COUNT(*) AS comment_count,
                            SUM(upvotes) AS total_upvotes,
                            SUM(downvotes) AS total_downvotes
                        FROM comments
                        WHERE deleted = false
                        GROUP BY user_id
                    ) c ON p.id = c.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS bookmark_count
                        FROM user_bookmarks
                        GROUP BY user_id
                    ) b ON p.id = b.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS upload_count
                        FROM uploads
                        GROUP BY user_id
                    ) up ON p.id = up.user_id
                    LEFT JOIN (
                        SELECT user_id, COUNT(*) AS list_count
                        FROM user_manga_lists
                        GROUP BY user_id
                    ) l ON p.id = l.user_id
                    WHERE p.id = @id";

                var profileDetails = await _postgresService.Connection.QueryFirstOrDefaultAsync<UserProfileDetailsResponse>(
                    query, new { id = userId });

                if (profileDetails == null)
                {
                    await _postgresService.CloseAsync();
                    return NotFound(ErrorResponse.Create("User not found", status: 404));
                }

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<UserProfileDetailsResponse>.Create(profileDetails));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user profile", ex.Message));
            }
        }

        /// <summary>
        /// Update user profile
        /// </summary>
        /// <param name="request">The update profile request containing new username and display name.</param>
        /// <returns>The update response.</returns>
        [HttpPut("profile")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Invalid request", "Validation failed"));
            }

            var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
            if (!string.IsNullOrEmpty(errorMessage))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
            }

            try
            {
                await _postgresService.OpenAsync();

                request.Username = request.Username.ToLower();

                if (Guid.TryParse(request.Username, out _))
                {
                    await _postgresService.CloseAsync();
                    return BadRequest(ErrorResponse.Create("Invalid request", "Username cannot be a UUID"));
                }

                var count = await _postgresService.Connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM profiles WHERE username = @username AND id != @id",
                    new { username = request.Username, id = userId });
                if (count > 0)
                {
                    await _postgresService.CloseAsync();
                    return BadRequest(ErrorResponse.Create("Invalid request", "Username already taken"));
                }

                await _postgresService.Connection.ExecuteAsync(
                    "UPDATE profiles SET username = @username, display_name = @display_name WHERE id = @id",
                    new { username = request.Username, display_name = request.DisplayName, id = userId });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Profile updated successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to update profile", ex.Message));
            }
        }
    }
}
