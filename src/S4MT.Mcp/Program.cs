using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using S4MT.Mcp;

if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Usage: S4MT.Mcp --config <absolute configuration.json>");
    return 2;
}

try
{
    ChildProcessJob.ContainHostLifetime();
    var options = S4MT.Mcp.HostOptions.Load(args[1]);
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<PathPolicy>();
    builder.Services.AddSingleton<CliProcessRunner>();
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ProjectTools>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or ArgumentException or McpFailure)
{
    Console.Error.WriteLine($"configuration_error: {exception.Message}");
    return 2;
}
