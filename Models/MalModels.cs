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
        public required string AccessToken { get; set; }
        public required string RefreshToken { get; set; }
        public required int ExpiresIn { get; set; }
        public required string TokenType { get; set; }
    }

    public class MalUpdateMangaListRequest
    {
        public required int MangaId { get; set; }
        public required int NumChaptersRead { get; set; }
    }

    public class MalMangaListStatus
    {
        public required string Status { get; set; }
        public required bool IsRereading { get; set; }
        public required int NumVolumesRead { get; set; }
        public required int NumChaptersRead { get; set; }
        public required int Score { get; set; }
        public required string UpdatedAt { get; set; }
        public required int Priority { get; set; }
        public required int NumTimesReread { get; set; }
        public required int RereadValue { get; set; }
        public required List<string> Tags { get; set; }
        public required string Comments { get; set; }
    }
}