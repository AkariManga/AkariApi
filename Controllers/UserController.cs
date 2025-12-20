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

                // Fetch user basic info from profiles table along with creation timestamp from auth.users
                var userQuery = @"
                    SELECT p.username, p.display_name, u.created_at
                    FROM profiles p
                    LEFT JOIN auth.users u ON p.id = u.id
                    WHERE p.id = @id";

                string username;
                string displayName;
                DateTimeOffset createdAt;

                using (var cmd = new NpgsqlCommand(userQuery, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("id", userId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await _postgresService.CloseAsync();
                        return NotFound(ErrorResponse.Create("User not found", status: 404));
                    }

                    username = reader.GetString(0);
                    displayName = reader.GetString(1);
                    createdAt = reader.IsDBNull(2) ? DateTimeOffset.UtcNow : reader.GetDateTime(2);
                }

                // Get total comments count (excluding deleted comments)
                int totalComments;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM comments WHERE user_id = @userId AND deleted = false", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    totalComments = result != null ? Convert.ToInt32(result) : 0;
                }

                // Get total upvotes and downvotes on user's comments
                int totalUpvotes = 0;
                int totalDownvotes = 0;
                using (var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(upvotes), 0), COALESCE(SUM(downvotes), 0) FROM comments WHERE user_id = @userId AND deleted = false", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        totalUpvotes = reader.GetInt32(0);
                        totalDownvotes = reader.GetInt32(1);
                    }
                }

                // Get total bookmarks count
                int totalBookmarks;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM user_bookmarks WHERE user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    totalBookmarks = result != null ? Convert.ToInt32(result) : 0;
                }

                // Get total uploads count
                int totalUploads;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM uploads WHERE user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    totalUploads = result != null ? Convert.ToInt32(result) : 0;
                }

                // Get total manga lists count
                int totalLists;
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM user_manga_lists WHERE user_id = @userId", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("userId", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    totalLists = result != null ? Convert.ToInt32(result) : 0;
                }

                var profileDetails = new UserProfileDetailsResponse
                {
                    UserId = userId,
                    Username = username,
                    DisplayName = displayName,
                    CreatedAt = createdAt,
                    TotalComments = totalComments,
                    TotalUpvotes = totalUpvotes,
                    TotalDownvotes = totalDownvotes,
                    TotalBookmarks = totalBookmarks,
                    TotalUploads = totalUploads,
                    TotalLists = totalLists
                };

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<UserProfileDetailsResponse>.Create(profileDetails));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                return StatusCode(500, ErrorResponse.Create("Failed to retrieve user profile", ex.Message));
            }
        }
    }
}