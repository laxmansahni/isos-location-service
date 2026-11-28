using ISOS.LocationService.Purge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

// Runs the purge once and exits: 0 when it finished (or another run was already in progress), 1 when it failed.
// A scheduler starts it daily (HLD Q9: the hosting platform is not chosen yet).
var builder = Host.CreateApplicationBuilder(args);

// Development only: reuse the API's git-ignored settings file so the database password is kept in one place (README).
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
    builder.Configuration.AddJsonFile(
        Path.Combine(builder.Environment.ContentRootPath, "..", "ISOS.LocationService", "appsettings.Local.json"), optional: true);
}

var connectionString = builder.Configuration.GetConnectionString("LocationDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:LocationDb is not configured.");
    return 1;
}

builder.Services.Configure<PurgeOptions>(builder.Configuration.GetSection(PurgeOptions.SectionName));
builder.Services.PostConfigure<PurgeOptions>(o =>
{
    // The API's setting decides which partitions still take inserts, so it must drive the purge too.
    o.RetentionDays = builder.Configuration.GetValue<int?>("Ingestion:RetentionDays") ?? o.RetentionDays;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PurgeService>();

using var host = builder.Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    await host.Services.GetRequiredService<PurgeService>().RunAsync(cancellation.Token);
    return 0;
}
catch (Exception ex)
{
    // PurgeService has already logged the failure and recorded it in purge_run.
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Purge").LogCritical(ex, "Purge failed");
    return 1;
}
