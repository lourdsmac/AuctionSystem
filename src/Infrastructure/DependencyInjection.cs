using AuctionSystem.Application.Abstractions;
using AuctionSystem.Infrastructure.Persistence;
using AuctionSystem.Infrastructure.Repositories;
using AuctionSystem.Infrastructure.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionSystem.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase("auction-demo"));

        services.AddScoped<IAuctionRepository, EfAuctionRepository>();
        services.AddSingleton<WebSocketConnectionManager>();
        services.AddSingleton<IAuctionRealtimeNotifier, AuctionWebSocketNotifier>();

        return services;
    }
}
