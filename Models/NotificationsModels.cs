using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class NotificationPayload
    {
        [Required]
        public required string Title { get; set; } = string.Empty;

        [Required]
        public required string Body { get; set; } = string.Empty;

        [Required]
        public required string Url { get; set; } = string.Empty;

        [Required]
        public required Guid MangaId { get; set; }
    }

    public class PushSubscriptionRequest
    {
        [Required]
        public required string Endpoint { get; set; } = string.Empty;

        [Required]
        public required string P256dh { get; set; } = string.Empty;

        [Required]
        public required string Auth { get; set; } = string.Empty;
    }
}
