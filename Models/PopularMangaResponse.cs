using System;

namespace AkariApi.Models
{
    public class PopularMangaResponse
    {
        public Guid Id { get; set; }
        public required string OrigId { get; set; }
        public required string Title { get; set; }
        public required string Cover { get; set; }
        public required string Description { get; set; }
        public required string Status { get; set; }
        public required string Type { get; set; }
        public required string[] Authors { get; set; }
        public required string[] Genres { get; set; }
        public required int Score { get; set; }
        public int? MalId { get; set; }
        public int? AniId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public required string[] AlternativeTitles { get; set; }
        public long ViewCount { get; set; }
        public long TotalCount { get; set; }
    }
}