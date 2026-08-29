using Microsoft.OpenApi;

namespace Vivarium.Controller.Rest.OpenApi;

public static class VivariumOpenApiExtensions
{
    public static IServiceCollection AddVivariumOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                foreach (var path in document.Paths.Keys
                    .Where(path => !path.StartsWith("/api/v1/", StringComparison.Ordinal))
                    .ToArray())
                {
                    document.Paths.Remove(path);
                }

                var publishedTags = document.Paths.Values
                    .SelectMany(path => path.Operations!.Values)
                    .SelectMany(operation => operation.Tags is null
                        ? Enumerable.Empty<OpenApiTagReference>()
                        : operation.Tags)
                    .Select(tag => tag.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(name => new OpenApiTag { Name = name })
                    .ToHashSet();

                document.Info = new OpenApiInfo
                {
                    Title = "Vivarium Management API",
                    Version = "v1",
                    Description =
                        "The canonical TeamCity and AgentExplorer management API. " +
                        "AgentHub gRPC and authenticated blob bytes remain separate data planes.",
                };
                document.Servers = [];
                document.Tags = publishedTags;
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapVivariumOpenApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi("/openapi/{documentName}.json")
            .ExcludeFromDescription();
        return endpoints;
    }
}
