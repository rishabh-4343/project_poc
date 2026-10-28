using Auditor.Agent;
using Auditor.Agent.Control;
using Auditor.Agent.Driver;
using Auditor.Agent.Logging;
using Auditor.Agent.Outbox;
using Auditor.Agent.Pipeline;
using Auditor.Agent.Storage;
using Auditor.Agent.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "Auditor Agent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

var agent = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
if (!string.IsNullOrWhiteSpace(agent.DataDirectory))
{
    builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(agent.ResolvedDataDirectory, "logs"), agent.LogRetentionDays));
}

switch (agent.DriverSource?.Trim().ToLowerInvariant())
{
    case "real":
        // Connect to the kernel minifilter's communication port. RealDriverSource and
        // RealDriverControl share one DriverConnection (the port allows a single client).
        builder.Services.AddSingleton<NtPathMap>();
        builder.Services.AddSingleton<DriverConnection>();
        builder.Services.AddSingleton<IDriverSource, RealDriverSource>();
        builder.Services.AddSingleton<IDriverControl, RealDriverControl>();
        break;
    case "simulated" or null or "":
        builder.Services.AddSingleton<IDriverSource, SimulatedDriverSource>();
        builder.Services.AddSingleton<IDriverControl, LoggingDriverControl>();
        break;
    default:
        throw new InvalidOperationException($"Unknown Agent:DriverSource '{agent.DriverSource}'. Use 'Simulated' or 'Real'.");
}
var dataDir = agent.ResolvedDataDirectory;
var crypto = new LocalCrypto(Path.Combine(dataDir, "state"));
builder.Services.AddSingleton(crypto);
builder.Services.AddSingleton(new LocalStore(Path.Combine(dataDir, "state"), crypto));
builder.Services.AddSingleton<SeqStore>();
builder.Services.AddSingleton(sp => new DiskOutbox(
    Path.Combine(dataDir, "outbox"), crypto, agent.OutboxMaxMegabytes * 1024L * 1024L, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Outbox")));
builder.Services.AddSingleton<PipelineChannels>();
builder.Services.AddSingleton<IAccountResolver, AccountResolver>();
builder.Services.AddSingleton<IProcessNameResolver, ProcessNameResolver>();
builder.Services.AddSingleton<ShareMap>();
builder.Services.AddSingleton<Enricher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScopeManager>();
builder.Services.AddSingleton<Router>();
builder.Services.AddSingleton<AgentStats>();
builder.Services.AddSingleton<IntegrityQueue>();
builder.Services.AddSingleton<CommandHandler>();
builder.Services.AddSingleton<AgentChannel>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentChannel>());
// Hosted services stop in reverse order: driver reader first, then correlator, then batcher, so a clean stop loses nothing.
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<UploadWorker>();
builder.Services.AddHostedService<BatcherWorker>();
builder.Services.AddHostedService<CorrelatorWorker>();
builder.Services.AddHostedService<DriverReader>();
builder.Services.AddHostedService<AgentWorker>();

builder.Build().Run();
