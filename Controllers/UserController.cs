using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Helpers;
using Supabase.Gotrue;
using AkariApi.Attributes;
using Npgsql;

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

                using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM profiles WHERE username = @username", _postgresService.Connection);
                cmd.Parameters.AddWithValue("username", request.UserName);
                var result = await cmd.ExecuteScalarAsync();
                var count = result != null ? Convert.ToInt64(result) : 0;
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
                    using var insertCmd = new NpgsqlCommand("INSERT INTO profiles (id, username, display_name) VALUES (@id, @username, @display_name)", _postgresService.Connection);
                    insertCmd.Parameters.AddWithValue("id", Guid.Parse(session.User.Id));
                    insertCmd.Parameters.AddWithValue("username", request.UserName);
                    insertCmd.Parameters.AddWithValue("display_name", request.DisplayName);
                    await insertCmd.ExecuteNonQueryAsync();
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

                // Fetch user profile from profiles table
                using var cmd = new NpgsqlCommand("SELECT username, display_name FROM profiles WHERE id = @id", _postgresService.Connection);
                cmd.Parameters.AddWithValue("id", userId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await _postgresService.CloseAsync();
                    return StatusCode(500, ErrorResponse.Create("Failed to sign in", "Profile not found"));
                }

                var userResponse = new UserResponse
                {
                    UserId = userId,
                    Username = reader.GetString(0),
                    DisplayName = reader.GetString(1)
                };

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

                // Reset authentication headers
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
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(SuccessResponse<UserResponse>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        [RequireTokenRefresh]
        public async Task<IActionResult> GetMe()
        {
            var token = AuthenticationHelper.GetAccessToken(Request);
            if (string.IsNullOrEmpty(token))
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", "Missing or invalid token"));
            }

            try
            {
                await _supabaseService.InitializeAsync();
                await _postgresService.OpenAsync();
                var user = await _supabaseService.Client.Auth.GetUser(token);
                if (user == null)
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", "Invalid token"));
                }

                if (string.IsNullOrEmpty(user.Id))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", "User ID not found"));
                }

                if (!Guid.TryParse(user.Id, out var userId))
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", "Invalid user ID format"));
                }

                // Fetch user profile from profiles table
                using var cmd = new NpgsqlCommand("SELECT username, display_name FROM profiles WHERE id = @id", _postgresService.Connection);
                cmd.Parameters.AddWithValue("id", userId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await _postgresService.CloseAsync();
                    return Unauthorized(ErrorResponse.Create("Unauthorized", "Profile not found"));
                }

                var userResponse = new UserResponse
                {
                    UserId = userId,
                    Username = reader.GetString(0),
                    DisplayName = reader.GetString(1)
                };

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

                // Fetch all user data in a single query for efficiency
                var query = @"
                    SELECT 
                        p.username, 
                        p.display_name, 
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

                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", userId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(ErrorResponse.Create("User not found", status: 404));
                    }

                    var profileDetails = new UserProfileDetailsResponse
                    {
                        UserId = userId,
                        Username = reader.GetString(0),
                        DisplayName = reader.GetString(1),
                        CreatedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                        TotalComments = reader.GetInt64(3),
                        TotalUpvotes = reader.GetInt64(4),
                        TotalDownvotes = reader.GetInt64(5),
                        TotalBookmarks = reader.GetInt64(6),
                        TotalUploads = reader.GetInt64(7),
                        TotalLists = reader.GetInt64(8)
                    };

                    return Ok(SuccessResponse<UserProfileDetailsResponse>.Create(profileDetails));
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user profile", ex.Message));
            }
            finally
            {
                await _postgresService.CloseAsync();
            }
        }
    }
}