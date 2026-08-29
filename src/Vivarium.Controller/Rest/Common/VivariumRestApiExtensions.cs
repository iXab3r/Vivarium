using Microsoft.AspNetCore.Http.Json;
using Vivarium.Controller.Rest.OpenApi;

namespace Vivarium.Controller.Rest.Common;

public static class VivariumRestApiExtensions
{
    public static IServiceCollection AddVivariumRestApi(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition =
                RestJson.SerializerOptions.DefaultIgnoreCondition;
            options.SerializerOptions.PropertyNamingPolicy = RestJson.SerializerOptions.PropertyNamingPolicy;
            options.SerializerOptions.WriteIndented = false;
            options.SerializerOptions.Converters.Clear();
            foreach (var converter in RestJson.SerializerOptions.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });
        services.AddSingleton<RestCursorCodec>();
        services.AddVivariumOpenApi();
        return services;
    }

    public static IApplicationBuilder UseVivariumRestApi(this IApplicationBuilder app) =>
        app.UseMiddleware<RestCorrelationMiddleware>();
}
