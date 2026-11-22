namespace GpBattleshipWebapp.Injectors;

public static class Injector
{
    /// <summary>
    /// Discovers every <see cref="IInjector"/> implementation in the app assembly and
    /// runs it, so adding a new installer is just a matter of adding a class.
    /// </summary>
    public static void InjectServicesFromAssemblies(this IServiceCollection services, IConfiguration configuration)
    {
        var injectors = typeof(Program).Assembly.ExportedTypes
            .Where(t => typeof(IInjector).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(Activator.CreateInstance).Cast<IInjector>()
            .ToList();

        injectors.ForEach(injector => injector.InjectServices(services, configuration));
    }
}
