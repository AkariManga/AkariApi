using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class PushSubscriptionRequest
    {
        [Required]
        public string Endpoint { get; set; } = string.Empty;

        [Required]
        public string P256dh { get; set; } = string.Empty;

        [Required]
        public string Auth { get; set; } = string.Empty;
    }
}