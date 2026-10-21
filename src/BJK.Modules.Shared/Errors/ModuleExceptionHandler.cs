using BJK.Modules.Shared.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Shared.Errors;

/// <summary>
/// Maps known module exceptions to RFC 7807 problem responses.
/// Anything else falls through to the host's default 500 handling.
/// </summary>
public sealed class ModuleExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException validation => new ValidationProblemDetails(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
            },
            NotFoundException notFound => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Resource not found.",
                Detail = notFound.Message,
            },
            DailyLimitReachedException daily => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Daily limit reached.",
                Detail = daily.Message,
                Extensions =
                {
                    ["code"] = DailyLimitReachedException.Code,
                    ["resetsAt"] = daily.ResetsAt,
                    ["limit"] = daily.Limit,
                },
            },
            TooManyRequestsException limited => new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Limit reached.",
                Detail = limited.Message,
                Extensions = { ["code"] = limited.Code },
            },
            ServiceUnavailableException unavailable => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service unavailable.",
                Detail = unavailable.Message,
            },
            ForbiddenException forbidden => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden.",
                Detail = forbidden.Message,
            },
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
