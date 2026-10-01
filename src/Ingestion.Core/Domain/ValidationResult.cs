namespace Ingestion.Core.Domain;

public sealed record ValidationResult<T>(T? Value, IReadOnlyList<string> Errors) where T : class
{
    public bool IsValid => Errors.Count == 0 && Value is not null;
}

public static class ValidationResult
{
    public static ValidationResult<T> Ok<T>(T value) where T : class => new(value, []);
    public static ValidationResult<T> Fail<T>(IReadOnlyList<string> errors) where T : class => new(null, errors);
}
