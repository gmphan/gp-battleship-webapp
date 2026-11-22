using GpBattleshipWebapp.Auth;
using GpBattleshipWebapp.Data;
using GpBattleshipWebapp.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GpBattleshipWebapp.Injectors;

public class DbInjector : IInjector
{
    public void InjectServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' is not configured. " +
                "Set ConnectionStrings__DefaultConnection in variables.env.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        // Auth options + merged (config + DB) admin repository.
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.AddScoped<IAdminRepository, AdminRepository>();
    }
}
