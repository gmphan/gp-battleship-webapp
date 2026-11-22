using GpBattleshipWebapp.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GpBattleshipWebapp.Data;

public static class StartupTasks
{
    /// <summary>
    /// Runs at startup: applies pending EF migrations (creating the schema on first
    /// run). Config admins (Auth:InitialAdmin / Auth:SeedAdmins) are resolved from
    /// configuration at runtime and always exist; they do NOT require DB seeding.
    ///
    /// Config (all optional):
    ///   Database:MigrateOnStartup  (default true)  — run migrations at boot.
    ///   Database:FailFast          (default: true in Production, false otherwise)
    ///       true  → a migration failure crashes startup (good for prod: don't serve
    ///               an app that can't persist admins).
    ///       false → log a clear, actionable warning and keep running (good for dev:
    ///               the site still loads and shows what's wrong).
    /// </summary>
    public static async Task InitializeAsync(WebApplication app)
    {
        var config = app.Configuration;
        var logger = app.Logger;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var migrate = config.GetValue("Database:MigrateOnStartup", true);
        var failFast = config.GetValue<bool?>("Database:FailFast")
                       ?? app.Environment.IsProduction();

        if (migrate)
        {
            try
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("Database migrations applied.");
            }
            catch (PostgresException pg) when (pg.SqlState == "42501")
            {
                // 42501 = insufficient_privilege. Almost always: the database doesn't
                // exist yet and the login can't CREATE DATABASE, or it isn't the owner.
                var dbName = db.Database.GetDbConnection().Database;
                logger.LogError(
                    pg,
                    "Cannot create/patch database '{Db}': the login lacks privileges (42501). " +
                    "Create it once, owned by your app user, then restart:\n" +
                    "    CREATE DATABASE \"{Db}\" OWNER <app_user>;\n" +
                    "(or grant CREATEDB to <app_user>). See README §6.1.",
                    dbName, dbName);
                if (failFast) throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Database migration failed. Admin login/management will be unavailable " +
                    "until the database is reachable and migrated. " +
                    "Set Database:FailFast=true to make this a hard startup failure.");
                if (failFast) throw;
            }
        }
        else
        {
            logger.LogWarning("Database:MigrateOnStartup=false — skipping migrations at boot.");
        }

        // Operator sanity: report config admins (passwords are never logged).
        var opts = scope.ServiceProvider.GetService<IOptions<AuthOptions>>()?.Value;
        var configAdmins = opts?.EffectiveConfigAdmins() ?? Array.Empty<ConfigAdminOptions>();
        if (configAdmins.Count == 0)
        {
            logger.LogWarning("No config admins found. Set Auth__InitialAdmin__Password (and optionally " +
                              "Auth__InitialAdmin__Username / Auth__SeedAdmins__N__*) in variables.env.");
        }
        else
        {
            foreach (var a in configAdmins)
                logger.LogInformation("Config admin available: {Username} [{Role}]", a.Username, a.Role);
        }
    }
}
