using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class UserResponse
    {
        [Required]
        public Guid UserId { get; set; } = Guid.NewGuid();

        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string DisplayName { get; set; } = string.Empty;
    }

    public class UserProfileDetailsResponse
    {
        [Required]
        public Guid UserId { get; set; }

        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string DisplayName { get; set; } = string.Empty;

        public DateTimeOffset? CreatedAt { get; set; }

        [Required]
        public long TotalComments { get; set; }

        [Required]
        public long TotalUpvotes { get; set; }

        [Required]
        public long TotalDownvotes { get; set; }

        [Required]
        public long TotalBookmarks { get; set; }

        [Required]
        public long TotalUploads { get; set; }

        [Required]
        public long TotalLists { get; set; }
    }
}