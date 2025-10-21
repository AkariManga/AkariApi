using Supabase;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.Mvc;
using AkariApi.Filters;
using System.Text.Json.Serialization;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v2", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "AkariApi v2", Version = "v2" });
    options.SchemaFilter<EnumSchemaFilter>();
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "AkariApi.xml"));
    if (builder.Environment.IsDevelopment())
    {
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "http://localhost:5188/" });
    }
    else
    {
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "https://api.akarimanga.dpdns.org/" });
    }
});

builder.Services.AddApiVersioning(options =>
{
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.DefaultApiVersion = new ApiVersion(2, 0);
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader()
    );
});

builder.Services.AddRateLimiter(_ => _
    .AddFixedWindowLimiter(policyName: "fixed", options =>
    {
        options.PermitLimit = 5;
        options.Window = TimeSpan.FromSeconds(10);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 2;
    }));

var url = builder.Configuration["SUPABASE_URL"] ?? Environment.GetEnvironmentVariable("SUPABASE_URL");
var key = builder.Configuration["SUPABASE_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_KEY");

if (string.IsNullOrEmpty(url))
{
    throw new InvalidOperationException("SUPABASE_URL environment variable is not set.");
}
if (string.IsNullOrEmpty(key))
{
    throw new InvalidOperationException("SUPABASE_KEY environment variable is not set.");
}

var options = new SupabaseOptions
{
    AutoRefreshToken = true,
    AutoConnectRealtime = true,
};
builder.Services.AddSingleton(provider => new Supabase.Client(url, key, options));
builder.Services.AddScoped<AkariApi.Services.SupabaseService>();

var app = builder.Build();

app.UseSwagger();
if (builder.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v2/swagger.json", "AkariApi v2");
    });
}

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseMiddleware<AkariApi.Middleware.TokenRefreshMiddleware>();

app.UseAuthorization();

app.MapControllers().RequireRateLimiting("fixed");

app.Run();
