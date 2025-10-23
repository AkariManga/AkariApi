using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class SignUpRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [MinLength(2)]
        [MaxLength(100)]
        [RegularExpression(@"^[a-z0-9-]+$", ErrorMessage = "Username must be fully lowercase and contain only dashes, lowercase letters, and numbers.")]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [MinLength(2)]
        [MaxLength(100)]
        public string DisplayName { get; set; } = string.Empty;
    }
}