using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.JellyPlay.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.JellyPlay.Tests;

/// <summary>
/// The plugin's API controllers are activated by Jellyfin's DI container —
/// a ctor parameter whose type the registrator never registers (directly or
/// as a hosted-service concrete) turns every route on that controller into a
/// 500 (the capabilities bootstrap probe was lost exactly this way). Pin: any
/// controller ctor parameter whose type lives in THIS plugin's assembly must
/// have a matching service registration.
/// </summary>
public class ControllerActivationTests
{
    [Fact]
    public void EveryPluginOwnedControllerCtorDependency_IsRegistered()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        var registered = services
            .Select(d => d.ServiceType)
            .Concat(services.Where(d => d.ImplementationFactory is not null).Select(_ => typeof(object)))
            .ToList();

        var assembly = typeof(JellyPlayController).Assembly;
        var controllers = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal) && !t.IsAbstract)
            .ToArray();

        Assert.NotEmpty(controllers);

        var failures = controllers
            .SelectMany(c => c.GetConstructors().SelectMany(k => k.GetParameters()), (c, p) => (Controller: c, Param: p.ParameterType))
            .Where(x => x.Param.Assembly == assembly)
            .Where(x => !registered.Contains(x.Param)
                        && !services.Any(d => d.ServiceType == x.Param
                                              || (d.ImplementationInstance?.GetType() == x.Param)))
            .Select(x => x.Controller.Name + " needs " + x.Param.Name)
            .ToList();

        Assert.True(failures.Count == 0, "Unregistered plugin-owned controller dependencies: " + string.Join(", ", failures));
    }

    [Fact]
    public void SimilarItemsProviderManager_IsAvailableAsConcreteAndHostedService()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        var manager = typeof(Services.Recommendations.SimilarItemsProviderManager);

        // The controller injects the concrete type...
        Assert.Contains(services, d => d.ServiceType == manager);
        // ...and the host starts it through IHostedService sharing that singleton.
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                                       && d.ImplementationFactory is not null);
    }
}
