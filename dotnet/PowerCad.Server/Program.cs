using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerCad.Core.Simulation;
using PowerCad.Core.Transport;
using PowerCad.Server;

var options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options.ShowHelp)
{
    Console.Out.WriteLine(ServerOptions.Usage);
    return 0;
}

if (options.ShowVersion)
{
    Console.Out.WriteLine($"power-cad-server {typeof(CadTools).Assembly.GetName().Version?.ToString(3)}");
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout belongs to MCP
builder.Logging.SetMinimumLevel(LogLevel.Warning);

ICadGateway gateway = options.Simulate
    ? new SimulatorGateway(InMemoryCadDocument.CreateSheetSample(), options.ReadOnly)
    : new PipeGateway(new DiscoveryStore(options.Home), options.Target, options.ReadOnly);
builder.Services.AddSingleton<ICadGateway>(new DocumentBoundGateway(gateway));
builder.Services.AddSingleton<IOntologyContextClient>(ContextClientFactory.FromEnvironment(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton<IVisualReasoner>(_ => NvidiaCosmosReasoner.FromEnvironment(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton<OntologyCandidateStore>();
builder.Services.AddSingleton(OntologyRestClient.FromEnvironment(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton(new PlanStore());

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "power-cad", Version = typeof(CadTools).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
        o.ServerInstructions = ServerOptions.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<CadTools>()
    .WithTools<OntologyContextTools>()
    .WithTools<OntologyRestTools>()
    .WithTools<SnapshotTools>()
    .WithTools<PlanTools>()
    .WithTools<ReviewTools>()
    .WithTools<HsSteelTools>();

await builder.Build().RunAsync();
return 0;
