using Ingestion.Api.Processing;
using Ingestion.Core.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Ingestion.Api.Endpoints;

public static class IngestionEndpoints
{
    public const string Route = "/api/v1/ingestions";

    /// <summary>
    /// The single business endpoint (ADR 0004). It responds 202 Accepted once records are
    /// validated and queued. Enrichment and delivery to Analytics happen asynchronously.
    /// </summary>
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, SubmitAsync)
            .WithName("SubmitIngestion")
            .Produces<IngestionResponse>(StatusCodes.Status202Accepted)
            .Produces<IngestionResponse>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> SubmitAsync(
        [FromBody] IngestionRequest? request,
        [FromHeader(Name = IngestionHeaders.IdempotencyKey)] string? idempotencyKey,
        IngestionService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var outcome = await service.SubmitAsync(request, idempotencyKey, cancellationToken);
        if (outcome is SubmitOutcome.Accepted { Replayed: true })
            http.Response.Headers[IngestionHeaders.IdempotentReplayed] = "true";
        switch (outcome)
        {
            case SubmitOutcome.Accepted { AllRejected: true } a:
                return Results.UnprocessableEntity(a.Response);
            case SubmitOutcome.Accepted a:
                return Results.Accepted(value: a.Response);
            case SubmitOutcome.BadRequest b:
                return Results.Problem(b.Detail, statusCode: StatusCodes.Status400BadRequest);
            case SubmitOutcome.IdempotencyConflict:
                return Results.Problem(
                    $"{IngestionHeaders.IdempotencyKey} was already used for a different payload.",
                    statusCode: StatusCodes.Status409Conflict);
            case SubmitOutcome.QueueFull:
                http.Response.Headers.RetryAfter = "60";
                return Results.Problem("Ingestion queue is full; retry later.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}");
        }
    }
}
