using Yarp.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Контроллеры (Gateway/Controllers/HealthController.cs) до этой правки не были
// зарегистрированы вообще — работал только YARP. Из-за этого GET /health/live
// перехватывался catch-all маршрутом "health-route" (/health/{**rest}) и
// проксировался в api наравне с /health/ready. Когда api-контейнер убит,
// оба эндпоинта падали одинаково, хотя liveness gateway'я не должен зависеть
// от того, жив ли backend (operation-persists-after-api-recreate это и проверяет:
// /health/live должен отвечать 200 даже пока api полностью недоступен).
builder.Services.AddControllers();

var app = builder.Build();

// Литеральный маршрут контроллера "/health/live" точнее catch-all шаблона YARP
// "/health/{**rest}", поэтому ASP.NET Core routing выберет именно его — но
// регистрируем MapControllers() первым для ясности и на случай будущих правок.
app.MapControllers();
app.MapReverseProxy();

app.Run();
