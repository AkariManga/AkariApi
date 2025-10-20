namespace AkariApi.Models
{
    public enum ResultType
    {
        Success,
        Error,
    }

    public class ApiResponse<T>
    {
        public required ResultType Result { get; set; }
        public required int Status { get; set; }
        public required T Data { get; set; }

        public static ApiResponse<T> Success(T data, int status = 200)
        {
            return new ApiResponse<T>
            {
                Result = ResultType.Success,
                Status = status,
                Data = data
            };
        }

        public static ApiResponse<ErrorData> Error(string message, object? details = null, int status = 500)
        {
            return new ApiResponse<ErrorData>
            {
                Result = ResultType.Error,
                Status = status,
                Data = new ErrorData { Message = message, Details = details }
            };
        }
    }

    public class PaginatedResponse<T>
    {
        public List<T> Items { get; set; } = new List<T>();
        public int TotalItems { get; set; }
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    }
}