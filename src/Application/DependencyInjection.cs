using AuctionSystem.Application.Abstractions;
using AuctionSystem.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionSystem.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuctionService>();
        return services;
    }
}
