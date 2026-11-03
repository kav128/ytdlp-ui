using Microsoft.AspNetCore.Antiforgery;

namespace Ytdlp.Ui.Infrastructure.Authentication;

public sealed class AdminAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: 400,
                    extensions: new Dictionary<string, object?> { ["code"] = "antiforgery_failed" }
                );
            }
        }

        return await next(context);
    }
}
