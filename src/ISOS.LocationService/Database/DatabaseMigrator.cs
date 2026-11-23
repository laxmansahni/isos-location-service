using System.Reflection;
using DbUp;

namespace ISOS.LocationService.Database;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies the embedded SQL migrations in name order. Each script runs once; DbUp records it in the
    /// schemaversions table. The same scripts run locally and against Aurora (ADR-010).
    /// </summary>
    public static void Migrate(string connectionString, ILogger logger)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }

        logger.LogInformation("Database migrations applied: {Count}", result.Scripts.Count());
    }
}
