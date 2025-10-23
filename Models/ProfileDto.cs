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
        public string UserName { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;
    }
}