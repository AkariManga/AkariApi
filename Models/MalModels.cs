using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public class MalTokenRequest
    {
        [Required]
        public required string Code { get; set; }
        [Required]
        public required string CodeVerifier { get; set; }
        [Required]
        public required string RedirectUri { get; set; }
    }

    public class MalTokenResponse
    {
        [JsonPropertyName("access_token")]
        [Required]
        public required string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        [Required]
        public required string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        [Required]
        public required int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        [Required]
        public required string TokenType { get; set; } = string.Empty;
    }

    public class MalUpdateMangaListRequest
    {
        [Required]
        public required int MangaId { get; set; }
        [Required]
        public required int NumChaptersRead { get; set; }
    }

    public class MalMangaListResponse
    {
        [Required]
        public required List<MalMangaListItem> Data { get; set; }
        [Required]
        public required MalPaging Paging { get; set; }
    }

    public class MalMangaListItem
    {
        [Required]
        public required MalMangaNode Node { get; set; }
        [Required]
        public MalListStatus ListStatus { get; set; } = new MalListStatus
        {
            Status = string.Empty,
            IsRereading = false,
            NumVolumesRead = 0,
            NumChaptersRead = 0,
            Score = 0,
            UpdatedAt = string.Empty
        };
    }

    public class MalMangaNode
    {
        [Required]
        public required int Id { get; set; }
        [Required]
        public required string Title { get; set; }
        public MalMainPicture? MainPicture { get; set; }
        [Required]
        public required string MediaType { get; set; }
    }

    public class MalMainPicture
    {
        [Required]
        public required string Medium { get; set; }
        [Required]
        public required string Large { get; set; }
    }

    public class MalListStatus
    {
        public string Status { get; set; } = string.Empty;
        public bool IsRereading { get; set; } = false;
        public int NumVolumesRead { get; set; } = 0;
        public int NumChaptersRead { get; set; } = 0;
        public int Score { get; set; } = 0;
        public string UpdatedAt { get; set; } = string.Empty;
    }

    public class MalMangaListStatus
    {
        [Required]
        public required string Status { get; set; } = string.Empty;
        [Required]
        public required bool IsRereading { get; set; } = false;
        [Required]
        public required int NumVolumesRead { get; set; } = 0;
        [Required]
        public required int NumChaptersRead { get; set; } = 0;
        [Required]
        public required int Score { get; set; } = 0;
        [Required]
        public required string UpdatedAt { get; set; } = string.Empty;
        [Required]
        public required int Priority { get; set; } = 0;
        [Required]
        public required int NumTimesReread { get; set; } = 0;
        [Required]
        public required int RereadValue { get; set; } = 0;
        [Required]
        public required List<string> Tags { get; set; } = new List<string>();
        [Required]
        public required string Comments { get; set; } = string.Empty;
    }

    public class MalPaging
    {
        public string? Next { get; set; }
    }

    public class MalUser
    {
        [Required]
        public required int Id { get; set; }
        [Required]
        public required string Name { get; set; }
        [Required]
        public string Location { get; set; } = string.Empty;
        [JsonPropertyName("joined_at")]
        [Required]
        public required string JoinedAt { get; set; }
    }
}