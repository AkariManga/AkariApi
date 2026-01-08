namespace AkariApi.Models
{
    public class ErrorData
    {
        public required string Message { get; set; }
        public string? Details { get; set; }
    }
}