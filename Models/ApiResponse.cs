using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AkariApi.Models
{
    public enum ResultType
    {
        Success,
        Error,
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "result")]
    [JsonDerivedType(typeof(SuccessResponse<>), "Success")]
    [JsonDerivedType(typeof(ErrorResponse), "Error")]
    public abstract class ApiResponse
    {
        [Required]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required ResultType Result { get; set; }

        [Required]
        public required int Status { get; set; }
    }

    public class SuccessResponse<T> : ApiResponse
    {
        [Required]
        public required T Data { get; set; }

        public static SuccessResponse<T> Create(T data, int status = 200)
        {
            return new SuccessResponse<T>
            {
                Result = ResultType.Success,
                Status = status,
                Data = data
            };
        }
    }

    public class ErrorResponse : ApiResponse
    {
        [Required]
        public required ErrorData Data { get; set; }

        public static ErrorResponse Create(string message, object? details = null, int status = 500)
        {
            return new ErrorResponse
            {
                Result = ResultType.Error,
                Status = status,
                Data = new ErrorData { Message = message, Details = details }
            };
        }
    }

    public class PaginatedResponse<T>
    {
        [Required]
        public List<T> Items { get; set; } = new List<T>();
        [Required]
        public int TotalItems { get; set; }
        [Required]
        public int CurrentPage { get; set; }
        [Required]
        public int PageSize { get; set; }
        [Required]
        public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    }
}