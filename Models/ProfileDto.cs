using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace AkariApi.Models
{
    [Table("profiles")]
    public class ProfileDto : BaseModel
    {
        [PrimaryKey("id")]
        public Guid Id { get; set; }

        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("role")]
        public string Role { get; set; } = "user";

        [Column("banned")]
        public bool Banned { get; set; } = false;
    }
}