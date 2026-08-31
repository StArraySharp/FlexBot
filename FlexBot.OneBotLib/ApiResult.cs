namespace OneBotLib
{
    public class ApiResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? StackTrace { get; set; }

        public static ApiResult Ok() => new() { Success = true };

        public static ApiResult Fail(string message, string? stackTrace = null)
            => new() { Success = false, ErrorMessage = message, StackTrace = stackTrace };

        public static ApiResult Fail(Exception ex)
            => new() { Success = false, ErrorMessage = ex.Message, StackTrace = ex.StackTrace };
    }

    public class ApiResult<T> : ApiResult
    {
        public T? Data { get; set; }

        public static ApiResult<T> Ok(T data) => new() { Success = true, Data = data };

        public new static ApiResult<T> Fail(string message, string? stackTrace = null)
            => new() { Success = false, ErrorMessage = message, StackTrace = stackTrace };

        public new static ApiResult<T> Fail(Exception ex)
            => new() { Success = false, ErrorMessage = ex.Message, StackTrace = ex.StackTrace };
    }
}
