using WebPush;
using AkariApi.Services;
using Npgsql;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using AkariApi.Helpers;

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
            var encryptionKey = _configuration["ENCRYPTION_KEY"];
            if (string.IsNullOrEmpty(encryptionKey))
            {
                throw new InvalidOperationException("Encryption key not configured");
            }

            var subscriptions = new List<(string endpoint, string p256dh, string auth, Guid userId, int unreadCount)>();
            var expiredEndpoints = new List<string>();

            try
            {
                await _postgresService.OpenAsync();

                // Single optimized query joining bookmarks, subscriptions, and getting unread count
                const string query = @"
                    SELECT DISTINCT
                        ps.endpoint,
                        ps.p256dh,
                        ps.auth,
                        ps.user_id,
                        COALESCE(
                            (SELECT COUNT(*)
                             FROM user_bookmarks_unread
                             WHERE user_id = ps.user_id),
                            0
                        ) as unread_count
                    FROM push_subscriptions ps
                    INNER JOIN user_bookmarks ub ON ps.user_id = ub.user_id
                    WHERE ub.manga_id = @mangaId";

                using (var cmd = new NpgsqlCommand(query, _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("mangaId", mangaId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            subscriptions.Add((
                                reader.GetString(0),  // endpoint
                                EncryptionHelper.Decrypt(reader.GetString(1), encryptionKey),  // p256dh
                                EncryptionHelper.Decrypt(reader.GetString(2), encryptionKey),  // auth
                                reader.GetGuid(3),     // user_id
                                reader.GetInt32(4)     // unread_count
                            ));
                        }
                    }
                }
            }
            finally
            {
                await _postgresService.CloseAsync();
            }

            if (subscriptions.Count == 0)
            {
                return;
            }

            // Send to each subscription in parallel
            var sendTasks = subscriptions.Select(async sub =>
            {
                try
                {
                    // Prepare payload with user-specific badge count
                    var payload = new
                    {
                        title,
                        body,
                        url,
                        mangaId = mangaId.ToString(),
                        tag = $"manga-{mangaId}",
                        badge = sub.unreadCount
                    };
                    var jsonPayload = JsonSerializer.Serialize(payload);

                    var subscription = new PushSubscription(sub.endpoint, sub.p256dh, sub.auth);
                    await _webPushClient.SendNotificationAsync(subscription, jsonPayload);
                }
                catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Subscription expired or invalid
                    lock (expiredEndpoints)
                    {
                        expiredEndpoints.Add(sub.endpoint);
                    }
                    Console.WriteLine($"Expired subscription removed: {sub.endpoint}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send notification to {sub.endpoint}: {ex.Message}");
                }
            });

            await Task.WhenAll(sendTasks);

            // Clean up expired subscriptions
            if (expiredEndpoints.Count > 0)
            {
                await RemoveExpiredSubscriptionsAsync(expiredEndpoints);
            }
        }

        private async Task RemoveExpiredSubscriptionsAsync(List<string> endpoints)
        {
            try
            {
                await _postgresService.OpenAsync();

                using (var cmd = new NpgsqlCommand("DELETE FROM push_subscriptions WHERE endpoint = ANY(@endpoints)", _postgresService.Connection))
                {
                    cmd.Parameters.AddWithValue("endpoints", endpoints.ToArray());
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to remove expired subscriptions: {ex.Message}");
            }
            finally
            {
                await _postgresService.CloseAsync();
            }
        }
    }
}