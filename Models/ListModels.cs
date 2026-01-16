using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class UserMangaListResponse
    {
        [Required]
        public Guid Id { get; set; }

        [Required]
        public Guid UserId { get; set; }

        [Required]
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Required]
        public bool IsPublic { get; set; }

        [Required]
        public DateTimeOffset CreatedAt { get; set; }

        [Required]
        public DateTimeOffset UpdatedAt { get; set; }

        [Required]
        public int TotalEntries { get; set; }
    }

    public class UserMangaListEntryResponse
    {
        [Required]
        public Guid Id { get; set; }

        [Required]
        public Guid ListId { get; set; }

        [Required]
        public Guid MangaId { get; set; }

        [Required]
        public int OrderIndex { get; set; }

        [Required]
        public DateTimeOffset CreatedAt { get; set; }

        [Required]
        public DateTimeOffset UpdatedAt { get; set; }

        [Required]
        public string MangaTitle { get; set; } = string.Empty;

        [Required]
        public string MangaCover { get; set; } = string.Empty;

        [Required]
        public string MangaDescription { get; set; } = string.Empty;
    }

    public class UserMangaListWithEntriesResponse : UserMangaListResponse
    {
        [Required]
        public List<UserMangaListEntryResponse> Entries { get; set; } = new List<UserMangaListEntryResponse>();

        [Required]
        public UserResponse User { get; set; } = new UserResponse();
    }

    public class UserMangaListPaginatedResponse : PaginatedResponse<UserMangaListResponse>
    {
    }

    public class CreateUserMangaListRequest
    {
        [Required]
        public required string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsPublic { get; set; } = false;
    }

    public class CreateUserMangaListEntryRequest
    {
        [Required]
        public required Guid MangaId { get; set; }
    }

    public class UpdateUserMangaListEntryRequest
    {
        [Required]
        public required int NewOrderIndex { get; set; }
    }
}
