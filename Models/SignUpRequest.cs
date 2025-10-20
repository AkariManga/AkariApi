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
        public string UserName { get; set; } = string.Empty;
    }
}