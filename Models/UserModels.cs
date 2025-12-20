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

        [Required]
        public DateTimeOffset CreatedAt { get; set; }

        [Required]
        public int TotalComments { get; set; }

        [Required]
        public int TotalUpvotes { get; set; }

        [Required]
        public int TotalDownvotes { get; set; }

        [Required]
        public int TotalBookmarks { get; set; }

        [Required]
        public int TotalUploads { get; set; }

        [Required]
        public int TotalLists { get; set; }
    }
}