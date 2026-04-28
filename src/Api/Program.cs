using AuctionSystem.Application;
using AuctionSystem.Infrastructure;
using AuctionSystem.Infrastructure.Persistence;
using Serilog;

namespace AuctionSystem.Api;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Bootstrapping AuctionSystem API");

            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog((ctx, services, cfg) =>
            {
                cfg
                    .ReadFrom.Configuration(ctx.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext();
            });

            var corsSection = builder.Configuration.GetSection("Cors:FrontendOrigins");
            var corsOrigins = corsSection.Exists()
                ? corsSection.Get<string[]>() ?? Array.Empty<string>()
                : new[] { "http://localhost:5173" };

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("Frontend", policy =>
                {
                    policy.WithOrigins(corsOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure();
            builder.Services.AddControllers();

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Auction.DbInit");
                AuctionDbInitializer.Initialize(db, logger);
            }

            app.UseWebSockets(new WebSocketOptions
            {
                // Keep pings reasonable for demo environments.
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            app.UseCors("Frontend");
            app.MapControllers();
            app.MapAuctionWebSocket();

            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
