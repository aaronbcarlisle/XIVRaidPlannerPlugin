namespace XIVRaidPlannerPlugin.Api;

public enum ApiError
{
    None,
    Unauthorized,   // 401/403 — bad/expired key
    NotFound,       // 404
    Network,        // connection/timeout/DNS
    Server,         // 5xx
    Unknown,
}

/// <summary>Outcome of an API call: a value on success, or a categorized error.</summary>
public readonly struct ApiResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public ApiError Error { get; }

    /// <summary>Server-provided error message (e.g. FastAPI's "detail"), when available.</summary>
    public string? Detail { get; }

    private ApiResult(bool ok, T? value, ApiError error, string? detail)
    {
        IsSuccess = ok; Value = value; Error = error; Detail = detail;
    }

    public static ApiResult<T> Ok(T value) => new(true, value, ApiError.None, null);
    public static ApiResult<T> Fail(ApiError error, string? detail = null) => new(false, default, error, detail);
}
