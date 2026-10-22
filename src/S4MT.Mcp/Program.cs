using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using S4MT.Cli;
using S4MT.Mcp;

if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Usage: S4MT.Mcp --config <absolute configuration.json>");
    return 2;
}

try
{
    var options = S4MT.Mcp.HostOptions.Load(args[1]);
    ChildProcessJob.ContainHostLifetime();

    if (options.Transport == "http")
    {
        var paths = new PathPolicy(options);
        var runner = new CliProcessRunner(options);
        var cache = new GameIndexStore(options);
        await cache.InitializeAsync(runner, CancellationToken.None);

        var webBuilder = WebApplication.CreateBuilder([]);
        webBuilder.WebHost.UseUrls(options.HttpListenUrl);
        webBuilder.Logging.ClearProviders();
        webBuilder.Logging.AddConsole(logging => logging.LogToStandardErrorThreshold = LogLevel.Trace);
        webBuilder.Services.AddSingleton(options);
        webBuilder.Services.AddSingleton(paths);
        webBuilder.Services.AddSingleton(runner);
        webBuilder.Services.AddSingleton(cache);
        webBuilder.Services.AddSingleton<CommandService>();
        webBuilder.Services.AddSingleton<ProjectTools>();
        webBuilder.Services.AddMcpServer()
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(CommandCatalog.All.Select(command => new CommandTool(command)));

        var app = webBuilder.Build();
        app.UseMiddleware<BearerTokenMiddleware>();
        HttpEndpoints.Map(app, options);
        await app.RunAsync();
        return 0;
    }

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new PathPolicy(options));
    builder.Services.AddSingleton<CliProcessRunner>();
    builder.Services.AddSingleton<CommandService>();
    builder.Services.AddSingleton<ProjectTools>();
    if (options.GameIndexCacheRoot is not null)
    {
        var cache = new GameIndexStore(options);
        await cache.InitializeAsync(new CliProcessRunner(options), CancellationToken.None);
        builder.Services.AddSingleton(cache);
    }
    builder.Services.AddMcpServer().WithStdioServerTransport()
        .WithTools(CommandCatalog.All.Select(command => new CommandTool(command)));
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or McpFailure)
{
    Console.Error.WriteLine($"configuration_error: {exception.Message}");
    return 2;
}
