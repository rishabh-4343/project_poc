namespace server.Common.Modules;

public interface IServerModule
{
    string Slug { get; }
    void Register(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder app);
    Task MigrateAsync(IServiceProvider services);
}

public static class ServerModuleExtensions
{
    public static WebApplicationBuilder AddModule<T>(this WebApplicationBuilder builder)
        where T : class, IServerModule, new()
    {
        var module = new T();
        module.Register(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<IServerModule>(module);
        return builder;
    }

    public static async Task MigrateModulesAsync(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<IServerModule>())
        {
            await module.MigrateAsync(app.Services);
        }
    }

    public static WebApplication MapModules(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<IServerModule>())
        {
            module.MapEndpoints(app);
        }

        return app;
    }
}
