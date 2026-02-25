using Microsoft.AspNetCore.Mvc;
using AkariApi.Models;
using AkariApi.Services;
using AkariApi.Attributes;
using AkariApi.Helpers;
using Dapper;
using Microsoft.Extensions.Configuration;

namespace AkariApi.Controllers
{
    [ApiController]
    [Route("v2/notifications")]
    [ApiVersion("2.0")]
    [Produces("application/json")]
    public class NotificationsController : ControllerBase
    {
        private readonly SupabaseService _supabaseService;
        private readonly PostgresService _postgresService;
        private readonly NotificationService _notificationService;
        private readonly IConfiguration _configuration;

        public NotificationsController(SupabaseService supabaseService, PostgresService postgresService, NotificationService notificationService, IConfiguration configuration)
        {
            _supabaseService = supabaseService;
            _postgresService = postgresService;
            _notificationService = notificationService;
            _configuration = configuration;
        }

        /// <summary>
        /// Subscribe to push notifications
        /// </summary>
        /// <param name="request">The subscription request containing endpoint, p256dh, and auth.</param>
        /// <returns>Success message.</returns>
        [HttpPost("subscribe")]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 401)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Bad Request", "Invalid request data"));
            }

            var encryptionKey = _configuration["ENCRYPTION_KEY"];
            if (string.IsNullOrEmpty(encryptionKey))
            {
                return StatusCode(500, ErrorResponse.Create("Server error", "Encryption key not configured"));
            }

            try
            {
                await _supabaseService.InitializeAsync();

                var (userId, errorMessage) = await AuthenticationHelper.AuthenticateAndSetSessionAsync(Request, _supabaseService);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return Unauthorized(ErrorResponse.Create("Unauthorized", errorMessage));
                }

                await _postgresService.OpenAsync();

                await _postgresService.Connection.ExecuteAsync(
                    "INSERT INTO push_subscriptions (user_id, endpoint, p256dh, auth) VALUES (@userId, @endpoint, @p256dh, @auth)",
                    new
                    {
                        userId,
                        endpoint = request.Endpoint,
                        p256dh = EncryptionHelper.Encrypt(request.P256dh, encryptionKey),
                        auth = EncryptionHelper.Encrypt(request.Auth, encryptionKey)
                    });

                await _postgresService.CloseAsync();
                return Ok(SuccessResponse<string>.Create("Subscribed successfully"));
            }
            catch (Exception ex)
            {
                await _postgresService.CloseAsync();
                if (ex.Message.Contains("duplicate"))
                {
                    return BadRequest(ErrorResponse.Create("Bad Request", "Subscription already exists"));
                }
                return StatusCode(500, ErrorResponse.Create("Failed to subscribe", ex.Message));
            }
        }

        /// <summary>
        /// Send push notification to users who bookmarked the manga
        /// </summary>
        /// <param name="request">The notification payload.</param>
        /// <returns>Success message.</returns>
        [HttpPost("send")]
        [DisableAnalytics]
        [ProducesResponseType(typeof(SuccessResponse<string>), 200)]
        [ProducesResponseType(typeof(ErrorResponse), 400)]
        [ProducesResponseType(typeof(ErrorResponse), 500)]
        public async Task<IActionResult> SendNotification([FromBody] NotificationPayload request)
        {
            var expectedApiKey = _configuration["API_KEY"];
            var providedApiKey = Request.Headers["X-API-Key"].ToString();
            if (string.IsNullOrEmpty(providedApiKey) || providedApiKey != expectedApiKey)
            {
                return Unauthorized(ErrorResponse.Create("Unauthorized", "Invalid API key"));
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ErrorResponse.Create("Bad Request", "Invalid request data"));
            }

            try
            {
                await _notificationService.SendNotificationToBookmarkedUsersAsync(request.MangaId, request.Title, request.Body, request.Url);
                return Ok(SuccessResponse<string>.Create("Notifications sent successfully"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ErrorResponse.Create("Failed to send notifications", ex.Message));
            }
        }
    }
}
