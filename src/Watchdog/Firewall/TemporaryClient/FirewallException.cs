using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
namespace TheKrystalShip.KGSM.Core.Models;
public class FirewallException : Exception
{
    public string? SocketPath { get; }
    public FirewallException() { }
    public FirewallException(string message) : base(message) { }
    public FirewallException(string message, string socketPath) : base(message) { SocketPath = socketPath; }
    public FirewallException(string message, string socketPath, Exception innerException) : base(message, innerException) { SocketPath = socketPath; }
}
public static class TemporaryFirewallRegistration
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddKgsmFirewallClient(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string socketPath)
    {
        services.AddSingleton<IFirewallService>(sp => new FirewallService(new FirewallClientOptions { SocketPath = socketPath }, sp.GetRequiredService<ILogger<FirewallService>>()));
        return services;
    }
}
