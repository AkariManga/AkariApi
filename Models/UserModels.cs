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
}