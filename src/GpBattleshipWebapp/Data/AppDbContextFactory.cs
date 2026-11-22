using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GpBattleshipWebapp.Data;

/// <summary>
/// Design-time factory used by `dotnet ef` (migrations add/update) so migrations can
/// be generated without booting the full app (which requires a live DB + auth config).
/// The connection string here is only used for scaffolding; runtime uses DI.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=gp_battleship;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(options);
    }
}
