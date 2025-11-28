using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class NotificationPayload
    {
        [Required]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Body { get; set; } = string.Empty;

        [Required]
        public string Url { get; set; } = string.Empty;

        [Required]
        public Guid MangaId { get; set; }
    }
}