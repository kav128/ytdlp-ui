namespace Ytdlp.Ui.Features.Hello;

public static class HelloEndpoints
{
    public static IEndpointRouteBuilder MapHelloEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api")
            .MapGet("/hello", () => TypedResults.Text("Hello World"))
            .WithName("Hello");

        return endpoints;
    }
}
