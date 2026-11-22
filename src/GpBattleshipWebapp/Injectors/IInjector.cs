namespace GpBattleshipWebapp.Injectors;

/// <summary>
/// Implemented by installer classes that register a specific slice of services
/// (database, auth, SignalR, …). All implementations are discovered and run by
/// <see cref="Injector.InjectServicesFromAssemblies"/>.
/// </summary>
public interface IInjector
{
    void InjectServices(IServiceCollection services, IConfiguration configuration);
}
