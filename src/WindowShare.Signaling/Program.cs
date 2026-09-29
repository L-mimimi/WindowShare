using WindowShare.Signaling.Hubs;
using WindowShare.Signaling.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddSingleton<RoomStore>();

// CORS：桌面客户端无浏览器跨域问题；此处允许全部便于调试，生产可收紧
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    .SetIsOriginAllowed(_ => true)));

var app = builder.Build();
app.UseCors();

// 健康检查（监控用）
app.MapGet("/", () => Results.Ok(new { service = "WindowShare Signaling", status = "ok" }));
app.MapGet("/stats", (RoomStore rooms) => Results.Ok(new { rooms = rooms.Count }));

app.MapHub<SignalingHub>("/signalr");

// 过期房间清理（每 30 秒）
var cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
_ = Task.Run(async () =>
{
    var store = app.Services.GetRequiredService<RoomStore>();
    while (await cleanupTimer.WaitForNextTickAsync())
        store.Cleanup();
});

app.Logger.LogInformation("WindowShare 信令服务器已启动: {Urls}", string.Join(", ", app.Urls));
app.Run();

