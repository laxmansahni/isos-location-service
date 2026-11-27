using ISOS.LocationService.Audit;
using ISOS.LocationService.Database;
using ISOS.LocationService.Identity;
using ISOS.LocationService.Ingestion;
using ISOS.LocationService.Policy;
using ISOS.LocationService.Query;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored file for the developer's own database password (see README). Development only.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<QueryOptions>(builder.Configuration.GetSection(QueryOptions.SectionName));
builder.Services.Configure<PolicyOptions>(builder.Configuration.GetSection(PolicyOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PointValidator>();
builder.Services.AddSingleton<PolicyEvaluator>();
builder.Services.AddScoped<PolicyService>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<ITimelineSource, LocationShareTimelineSource>();
builder.Services.AddScoped<ReplayService>();

// The connection string comes from user secrets or environment variables, never from a committed file.
var connectionString = builder.Configuration.GetConnectionString("LocationDb");

// "Testing" is the environment the automated tests run in. Unlike Development it loads neither user secrets nor
// appsettings.Local.json, so a developer's own database settings never leak into the tests.
var devShortcuts = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

// Development only: skip the database and keep points in memory (lost on restart).
var forceInMemory = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Database:UseInMemory");
var usePostgres = !forceInMemory && !string.IsNullOrWhiteSpace(connectionString);
if (usePostgres)
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString!));
    builder.Services.AddSingleton<ILocationPointStore, PostgresLocationPointStore>();
    builder.Services.AddSingleton<ILocationQueryStore, PostgresLocationQueryStore>();
    builder.Services.AddSingleton<IAccessAuditStore, PostgresAccessAuditStore>();
    builder.Services.AddSingleton<IPolicyStore, PostgresPolicyStore>();
    builder.Services.AddSingleton<PartitionMaintenance>();
    builder.Services.AddHostedService<PartitionMaintenanceService>();
}
else if (devShortcuts)
{
    // Development and test convenience only: points are lost on restart.
    builder.Services.AddSingleton<InMemoryLocationPointStore>();
    builder.Services.AddSingleton<ILocationPointStore>(sp => sp.GetRequiredService<InMemoryLocationPointStore>());
    builder.Services.AddSingleton<ILocationQueryStore>(sp => sp.GetRequiredService<InMemoryLocationPointStore>());
    builder.Services.AddSingleton<InMemoryAccessAuditStore>();
    builder.Services.AddSingleton<IAccessAuditStore>(sp => sp.GetRequiredService<InMemoryAccessAuditStore>());
    builder.Services.AddSingleton<InMemoryPolicyStore>();
    builder.Services.AddSingleton<IPolicyStore>(sp => sp.GetRequiredService<InMemoryPolicyStore>());
}
else
{
    throw new InvalidOperationException("ConnectionStrings:LocationDb is not configured.");
}

if (devShortcuts)
{
    builder.Services.AddScoped<ICallerContext, DevelopmentHeaderCallerContext>();
}
else
{
    builder.Services.AddScoped<ICallerContext, UnauthenticatedCallerContext>();
}

var app = builder.Build();

if (usePostgres)
{
    // Migrations run on startup only where enabled (Development). Elsewhere they are a separate deployment step.
    if (app.Configuration.GetValue<bool>("Database:RunMigrationsOnStartup"))
    {
        DatabaseMigrator.Migrate(connectionString!, app.Logger);
    }

    // Inserts fail for a day with no partition, so create them before serving requests.
    await app.Services.GetRequiredService<PartitionMaintenance>().EnsureAsync(CancellationToken.None);
}
else
{
    app.Logger.LogWarning("ConnectionStrings:LocationDb is not set; using the in-memory store. Data is lost on restart.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Needed by the integration tests.
public partial class Program;
