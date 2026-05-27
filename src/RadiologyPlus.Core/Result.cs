namespace RadiologyPlus.Core;

/// <summary>
/// Lightweight result type. Use at boundaries where exception-throwing would be too heavy
/// (e.g., login validation, validation step failures the UI needs to render).
/// Internal/programmer errors should still throw.
/// </summary>
public readonly record struct Result<T>(bool IsSuccess, T? Value, string? Error, string? ErrorCode);

public static class Result
{
    public static Result<T> Ok<T>(T value) => new(true, value, null, null);
    public static Result<T> Fail<T>(string error, string? code = null) => new(false, default, error, code);
}
