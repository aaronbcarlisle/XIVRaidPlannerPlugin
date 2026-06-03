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

    private ApiResult(bool ok, T? value, ApiError error)
    {
        IsSuccess = ok; Value = value; Error = error;
    }

    public static ApiResult<T> Ok(T value) => new(true, value, ApiError.None);
    public static ApiResult<T> Fail(ApiError error) => new(false, default, error);
}
