using System.ComponentModel.DataAnnotations;

namespace AkariApi.Models
{
    public class AniUserResponse
    {
        [Required]
        public required AniUserData Data { get; set; }
    }

    public class AniUserData
    {
        [Required]
        public required AniViewer Viewer { get; set; }
    }

    public class AniViewer
    {
        [Required]
        public required int Id { get; set; }
        [Required]
        public required string Name { get; set; }
    }

    public class AniMangaListResponse
    {
        [Required]
        public required AniMangaListData Data { get; set; }
    }

    public class AniMangaListData
    {
        [Required]
        public required AniMediaListCollection MediaListCollection { get; set; }
    }

    public class AniMediaListCollection
    {
        [Required]
        public required List<AniList> Lists { get; set; }
    }

    public class AniList
    {
        [Required]
        public required string Name { get; set; }
        [Required]
        public required List<AniEntry> Entries { get; set; }
    }

    public class AniEntry
    {
        [Required]
        public required long Id { get; set; }
        [Required]
        public required int Score { get; set; }
        [Required]
        public required int Progress { get; set; }
        [Required]
        public required string Status { get; set; }
        [Required]
        public required AniMedia Media { get; set; }
    }

    public class AniMedia
    {
        [Required]
        public required int Id { get; set; }
        [Required]
        public required AniTitle Title { get; set; }
    }

    public class AniTitle
    {
        public string? English { get; set; }
    }

    public class AniUpdateMangaListRequest
    {
        [Required]
        public required int MediaId { get; set; }
        [Required]
        public required int Progress { get; set; }
    }

    public class AniUpdateResponse
    {
        [Required]
        public required AniUpdateData Data { get; set; }
    }

    public class AniUpdateData
    {
        [Required]
        public required AniUpdatedEntry SaveMediaListEntry { get; set; }
    }

    public class AniUpdatedEntry
    {
        [Required]
        public required long Id { get; set; }
        [Required]
        public required string Status { get; set; }
        [Required]
        public required int Progress { get; set; }
    }
}