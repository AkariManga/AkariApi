using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Helpers;
using Supabase.Gotrue;
using AkariApi.Attributes;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/user")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class UserController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;

        public UserController(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        /// <summary>
        /// Create a user
        /// </summary>
        /// <param name="request">The signup request containing email and password.</param>
        /// <returns>The signup response.</returns>
        [HttpPost("signup")]
        [ProducesResponseType(typeof(ApiResponse<Session>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Invalid request", "Validation failed"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var existingUser = await _supabaseService.Client
                    .From<ProfileDto>()
                    .Select("username")
                    .Where(u => u.Username == request.UserName)
                    .Get();
                if (existingUser != null && existingUser.Models.Count > 0)
                {
                    return BadRequest(ApiResponse<ErrorData>.Error("Invalid request", "Username already taken"));
                }

                var options = new Supabase.Gotrue.SignUpOptions
                {
                    Data = new Dictionary<string, object> { { "username", request.UserName }, { "display_name", request.DisplayName } }
                };
                var session = await _supabaseService.Client.Auth.SignUp(request.Email, request.Password, options);

                if (session == null)
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign up", "User creation returned null"));
                }

                if (!string.IsNullOrEmpty(session.AccessToken))
                {
                    CookieHelper.SetCookie(Response, "accessToken", session.AccessToken, expires: TimeSpan.FromDays(365));
                    if (!string.IsNullOrEmpty(session.RefreshToken))
                    {
                        CookieHelper.SetCookie(Response, "refreshToken", session.RefreshToken, expires: TimeSpan.FromDays(365));
                    }
                }

                return Ok(ApiResponse<Session>.Success(session));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign up", ex.Message));
            }
        }

        /// <summary>
        /// Sign in a user
        /// </summary>
        /// <param name="request">The signin request containing email and password.</param>
        /// <returns>The signin response.</returns>
        [HttpPost("signin")]
        [ProducesResponseType(typeof(ApiResponse<UserResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 400)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public async Task<IActionResult> SignIn([FromBody] SignInRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<ErrorData>.Error("Invalid request", "Validation failed"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var session = await _supabaseService.Client.Auth.SignIn(request.Email, request.Password);

                if (session == null)
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign in", "Authentication returned null"));
                }

                if (session.User == null || string.IsNullOrEmpty(session.User.Id))
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign in", "User ID not found in session"));
                }

                if (!Guid.TryParse(session.User.Id, out var userId))
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign in", "Invalid user ID format"));
                }

                // Fetch user profile from profiles table
                var profile = await _supabaseService.Client
                    .From<ProfileDto>()
                    .Where(p => p.Id == userId)
                    .Single();

                if (profile == null)
                {
                    return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign in", "Profile not found"));
                }

                var userResponse = new UserResponse
                {
                    UserId = profile.Id,
                    Username = profile.Username,
                    DisplayName = profile.DisplayName
                };

                if (!string.IsNullOrEmpty(session.AccessToken))
                {
                    CookieHelper.SetCookie(Response, "accessToken", session.AccessToken, expires: TimeSpan.FromDays(365));
                    if (!string.IsNullOrEmpty(session.RefreshToken))
                    {
                        CookieHelper.SetCookie(Response, "refreshToken", session.RefreshToken, expires: TimeSpan.FromDays(365));
                    }
                }

                return Ok(ApiResponse<UserResponse>.Success(userResponse));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign in", ex.Message));
            }
        }

        /// <summary>
        /// Sign out a user
        /// </summary>
        /// <returns>The signout response.</returns>
        [HttpPost("signout")]
        [ProducesResponseType(typeof(ApiResponse<string>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        public new async Task<IActionResult> SignOut()
        {
            try
            {
                await _supabaseService.InitializeAsync();

                await _supabaseService.Client.Auth.SignOut();

                // Reset authentication headers
                Response.Cookies.Delete("accessToken");
                Response.Cookies.Delete("refreshToken");

                return Ok(ApiResponse<string>.Success("Signed out successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<ErrorData>.Error("Failed to sign out", ex.Message));
            }
        }

        /// <summary>
        /// Get current user
        /// </summary>
        /// <returns>The user information.</returns>
        [HttpGet("me")]
        [CacheControl(CacheDuration.NoCache, CacheDuration.NoCache, false)]
        [ProducesResponseType(typeof(ApiResponse<UserResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 401)]
        [ProducesResponseType(typeof(ApiResponse<ErrorData>), 500)]
        [RequireTokenRefresh]
        public async Task<IActionResult> GetMe()
        {
            var token = AuthenticationHelper.GetAccessToken(Request);
            if (string.IsNullOrEmpty(token))
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", "Missing or invalid token"));
            }

            try
            {
                await _supabaseService.InitializeAsync();
                var user = await _supabaseService.Client.Auth.GetUser(token);
                if (user == null)
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", "Invalid token"));
                }

                if (string.IsNullOrEmpty(user.Id))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", "User ID not found"));
                }

                if (!Guid.TryParse(user.Id, out var userId))
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", "Invalid user ID format"));
                }

                // Fetch user profile from profiles table
                var profile = await _supabaseService.Client
                    .From<ProfileDto>()
                    .Where(p => p.Id == userId)
                    .Single();

                if (profile == null)
                {
                    return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", "Profile not found"));
                }

                var userResponse = new UserResponse
                {
                    UserId = profile.Id,
                    Username = profile.Username,
                    DisplayName = profile.DisplayName
                };

                return Ok(ApiResponse<UserResponse>.Success(userResponse));
            }
            catch (Exception ex)
            {
                return Unauthorized(ApiResponse<ErrorData>.Error("Unauthorized", ex.Message));
            }
        }
    }
}