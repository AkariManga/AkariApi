using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public class MalTokenRequest
    {
        public required string Code { get; set; }
        public required string CodeVerifier { get; set; }
        public required string RedirectUri { get; set; }
    }

    public class MalTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    public class MalUpdateMangaListRequest
    {
        public required int MangaId { get; set; }
        public required int NumChaptersRead { get; set; }
    }

    public class MalMangaListResponse
    {
        public required List<MalMangaListItem> Data { get; set; }
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
        [Required]
        public MalMainPicture? MainPicture { get; set; }
        [Required]
        public required string MediaType { get; set; }
    }

    public class MalMainPicture
    {
        public required string Medium { get; set; }
        public required string Large { get; set; }
    }

    public class MalListStatus
    {
        [Required]
        public string Status { get; set; } = string.Empty;
        [Required]
        public bool IsRereading { get; set; } = false;
        [Required]
        public int NumVolumesRead { get; set; } = 0;
        [Required]
        public int NumChaptersRead { get; set; } = 0;
        [Required]
        public int Score { get; set; } = 0;
        [Required]
        public string UpdatedAt { get; set; } = string.Empty;
    }

    public class MalMangaListStatus
    {
        public string Status { get; set; } = string.Empty;
        public bool IsRereading { get; set; } = false;
        public int NumVolumesRead { get; set; } = 0;
        public int NumChaptersRead { get; set; } = 0;
        public int Score { get; set; } = 0;
        public string UpdatedAt { get; set; } = string.Empty;
        public int Priority { get; set; } = 0;
        public int NumTimesReread { get; set; } = 0;
        public int RereadValue { get; set; } = 0;
        public List<string> Tags { get; set; } = new List<string>();
        public string Comments { get; set; } = string.Empty;
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