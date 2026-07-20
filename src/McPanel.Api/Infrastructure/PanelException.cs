using Microsoft.AspNetCore.Mvc;

namespace McPanel.Api.Infrastructure;

public sealed class PanelException(int statusCode, string code, string title, string? detail = null) : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
}

public static class PanelProblems
{
    public static IResult Result(PanelException exception, HttpContext context) => Results.Problem(new ProblemDetails
    {
        Status = exception.StatusCode,
        Title = exception.Title,
        Detail = exception.Detail,
        Instance = context.Request.Path,
        Extensions = { ["code"] = exception.Code, ["traceId"] = context.TraceIdentifier }
    });

    public static PanelException NotFound(string resource = "Resource") =>
        new(StatusCodes.Status404NotFound, "NOT_FOUND", $"{resource} was not found.");

    public static PanelException Validation(string detail) =>
        new(StatusCodes.Status400BadRequest, "VALIDATION_FAILED", "The request is invalid.", detail);

    public static PanelException Conflict(string code, string title, string? detail = null) =>
        new(StatusCodes.Status409Conflict, code, title, detail);

    public static PanelException BadRequest(BadHttpRequestException exception) =>
        exception.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? new(StatusCodes.Status413PayloadTooLarge, "FILE_TOO_LARGE", "The request body exceeds the configured limit.")
            : new(exception.StatusCode, "VALIDATION_FAILED", "The request is invalid.", exception.Message);
}
