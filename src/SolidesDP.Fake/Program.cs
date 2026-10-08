using SolidesDP.Fake;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFake();

var app = builder.Build();
app.UseFake();
app.Run();
