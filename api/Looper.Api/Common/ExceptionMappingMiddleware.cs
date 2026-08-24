using FluentValidation;

namespace Looper.Api.Common;

/// <summary>Translates domain and validation exceptions thrown by handlers into problem responses.</summary>
public sealed class ExceptionMappingMiddleware(RequestDelegate next, ILogger<ExceptionMappingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            await Results.ValidationProblem(errors).ExecuteAsync(context);
        }
        catch (NotFoundException ex)
        {
            await Results.Problem(title: ex.Message, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
        }
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Request aborted by client");
        }
    }
}
