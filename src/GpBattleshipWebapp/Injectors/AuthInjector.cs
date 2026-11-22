using GpBattleshipWebapp.Auth;

namespace GpBattleshipWebapp.Injectors;

/// <summary>Registers cookie authentication + authorization for the admin surface.</summary>
public class AuthInjector : IInjector
{
    public void InjectServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddBattleshipAuth();
    }
}
