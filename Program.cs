using Newtonsoft.Json.Serialization;
using WatchPartyServer.Hubs;
using WatchPartyServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSignalR()
    .AddNewtonsoftJsonProtocol(options =>
    {
        options.PayloadSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
    });

builder.Services.AddSingleton<RoomStateManager>();
builder.Services.AddSingleton<IRoomStateManager>(sp => sp.GetRequiredService<RoomStateManager>());
builder.Services.AddSingleton<IAdminStateManager>(sp => sp.GetRequiredService<RoomStateManager>());
builder.Services.AddHostedService<StateBroadcastService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors();

app.MapHub<WatchPartyHub>("/hubs/watchparty");
app.MapHealthChecks("/health");

Console.WriteLine("╔════════════════════════════════════════════╗");
Console.WriteLine("║     Watch Party Server Started             ║");
Console.WriteLine("║     Hub: /hubs/watchparty                  ║");
Console.WriteLine("║     Health: /health                        ║");
Console.WriteLine("╚════════════════════════════════════════════╝");

app.Run();
