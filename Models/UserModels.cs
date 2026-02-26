using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public enum UserSortBy
    {
        CreatedAt,
        Username,
        TotalComments,
        TotalUpvotes,
        TotalBookmarks,
        TotalUploads,
        TotalLists
    }

    public enum SortDirection
    {
        Ascending,
        Descending
    }

    public enum UserRole
    {
        user,
        admin,
        moderator,
        owner
    }

    public class UserResponse
    {
        [Required]
        public Guid UserId { get; set; } = Guid.NewGuid();

        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        public UserRole Role { get; set; } = UserRole.user;

        [Required]
        public bool Banned { get; set; } = false;
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
        public UserRole Role { get; set; } = UserRole.user;

        [Required]
        public bool Banned { get; set; } = false;

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

    public class UpdateProfileRequest
    {
        [Required]
        public required string Username { get; set; } = string.Empty;

        [Required]
        public required string DisplayName { get; set; } = string.Empty;
    }

    public class SignUpRequest
    {
        [Required]
        [EmailAddress]
        public required string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public required string Password { get; set; } = string.Empty;

        [Required]
        [MinLength(2)]
        [MaxLength(100)]
        [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Username must be fully lowercase and contain only dashes, lowercase letters, and numbers.")]
        public required string UserName { get; set; } = string.Empty;

        [Required]
        [MinLength(2)]
        [MaxLength(100)]
        public required string DisplayName { get; set; } = string.Empty;
    }

    public class SignInRequest
    {
        [Required]
        [EmailAddress]
        public required string Email { get; set; } = string.Empty;

        [Required]
        public required string Password { get; set; } = string.Empty;
    }
}