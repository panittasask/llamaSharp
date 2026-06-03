using llamaCPGGUFllm.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<LlmConfiguration>(builder.Configuration.GetSection("LlmConfiguration"));

// ใช้ HttpClient คุยกับ llama-server (llama.cpp) — ตั้ง Timeout ยาว ๆ เผื่อ generate นาน
builder.Services.AddHttpClient<LlmService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
