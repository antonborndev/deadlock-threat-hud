using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class
    DeadlockHeroCatalogEndpoints
{
    public static void MapDeadlockHeroCatalogEndpoints(
        this WebApplication app,
        DeadlockHeroCatalogService service
    )
    {
        app.MapPost(
            "/deadlock-api/resolve-heroes",
            async (
                HeroResolveRequest request,
                HttpContext context
            ) =>
            {
                if (
                    request.Names is null ||
                    request.Names.Length == 0
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "The names field must contain " +
                                "at least one hero name."
                        }
                    );
                }

                if (
                    request.Names.Length > 100
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            ok = false,

                            error =
                                "A single request allows " +
                                "no more than 100 names."
                        }
                    );
                }

                try
                {
                    var resolutions =
                        await service.ResolveAsync(
                            request.Names,
                            context.RequestAborted
                        );

                    return Results.Json(
                        new
                        {
                            ok = true,

                            requested =
                                resolutions.Count,

                            resolved =
                                resolutions.Count(
                                    result =>
                                        result.Status ==
                                        "resolved"
                                ),

                            unknown =
                                resolutions.Count(
                                    result =>
                                        result.Status ==
                                        "unknown"
                                ),

                            ambiguous =
                                resolutions.Count(
                                    result =>
                                        result.Status ==
                                        "ambiguous"
                                ),

                            heroes =
                                resolutions
                        }
                    );
                }
                catch (
                    OperationCanceledException
                )
                {
                    return Results.Problem(
                        title:
                            "Hero catalog request was canceled.",

                        statusCode:
                            499
                    );
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        error
                    );

                    return Results.Problem(
                        title:
                            "Hero catalog error " +
                            "Deadlock API.",

                        detail:
                            error.Message,

                        statusCode:
                            StatusCodes
                                .Status502BadGateway
                    );
                }
            }
        );
    }
}

internal sealed record HeroResolveRequest(
    string[]? Names
);