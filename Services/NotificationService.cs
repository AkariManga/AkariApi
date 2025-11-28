using WebPush;
using AkariApi.Services;
using Npgsql;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AkariApi.Services
{
    public class NotificationService
    {
        private readonly PostgresService _postgresService;
        private readonly IConfiguration _configuration;
        private readonly WebPushClient _webPushClient;

        public NotificationService(PostgresService postgresService, IConfiguration configuration)
        {
            _postgresService = postgresService;
            _configuration = configuration;
            _webPushClient = new WebPushClient();
            var vapidSubject = _configuration["WEBPUSH_SUBJECT"];
            var vapidPublicKey = _configuration["VAPID_PUBLIC_KEY"];
            var vapidPrivateKey = _configuration["VAPID_PRIVATE_KEY"];
            _webPushClient.SetVapidDetails(vapidSubject, vapidPublicKey, vapidPrivateKey);
        }

        public async Task SendNotificationToBookmarkedUsersAsync(Guid mangaId, string title, string body, string url)
        {
            await _postgresService.OpenAsync();

            // Get user_ids who have this manga bookmarked
            var userIds = new List<Guid>();
            using (var cmd = new NpgsqlCommand("SELECT user_id FROM user_bookmarks WHERE manga_id = @mangaId", _postgresService.Connection))
            {
                cmd.Parameters.AddWithValue("mangaId", mangaId);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        userIds.Add(reader.GetGuid(0));
                    }
                }
            }

            if (userIds.Count == 0)
            {
                await _postgresService.CloseAsync();
                return;
            }

            // For each user, get their subscriptions
            var subscriptions = new List<(string endpoint, string p256dh, string auth)>();
            using (var cmd = new NpgsqlCommand("SELECT endpoint, p256dh, auth FROM push_subscriptions WHERE user_id = ANY(@userIds)", _postgresService.Connection))
            {
                cmd.Parameters.AddWithValue("userIds", userIds.ToArray());
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        subscriptions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }
                }
            }

            await _postgresService.CloseAsync();

            // Prepare payload
            var payload = new
            {
                title,
                body,
                url,
                mangaId = mangaId.ToString(),
                tag = $"manga-{mangaId}"
            };
            var jsonPayload = JsonSerializer.Serialize(payload);

            // Send to each subscription
            var tasks = subscriptions.Select(async sub =>
            {
                try
                {
                    var subscription = new PushSubscription(sub.endpoint, sub.p256dh, sub.auth);
                    await _webPushClient.SendNotificationAsync(subscription, jsonPayload);
                }
                catch (Exception ex)
                {
                    // Log error, but continue
                    Console.WriteLine($"Failed to send notification: {ex.Message}");
                }
            });
            await Task.WhenAll(tasks);
        }
    }
}