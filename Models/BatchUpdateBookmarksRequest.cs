using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class BatchUpdateBookmarksRequest
    {
        [Required]
        public List<BatchUpdateBookmarkItem> Items { get; set; } = new();
    }

    public class BatchUpdateBookmarkItem
    {
        [Required]
        public Guid MangaId { get; set; }

        [Required]
        public Guid ChapterId { get; set; }
    }
}