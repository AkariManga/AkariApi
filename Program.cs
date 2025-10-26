using Supabase;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.Mvc;
using AkariApi.Filters;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

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
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "http://localhost:5188" });
    }
    else
    {
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "https://api.akarimanga.dpdns.org" });
    }
    options.AddSecurityDefinition("CookieAuth", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Cookie,
        Name = "accessToken",
    });
    options.AddSecurityDefinition("MalCookieAuth", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Cookie,
        Name = "mal_access_token",
    });
    options.OperationFilter<AkariApi.Filters.AuthorizeCheckOperationFilter>();
    options.OperationFilter<AkariApi.Filters.MalAuthorizeCheckOperationFilter>();
    options.OperationFilter<AkariApi.Filters.RemoveExtraContentTypesOperationFilter>();
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
    })
    .AddFixedWindowLimiter(policyName: "uploads", options =>
    {
        options.PermitLimit = 1;
        options.Window = TimeSpan.FromMinutes(1);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 1;
    }));

var supabaseUrl = builder.Configuration["SUPABASE_URL"] ?? Environment.GetEnvironmentVariable("SUPABASE_URL");
var anonKey = builder.Configuration["SUPABASE_ANON_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY");
var serviceRoleKey = builder.Configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

if (string.IsNullOrEmpty(supabaseUrl))
{
    throw new InvalidOperationException("SUPABASE_URL environment variable is not set.");
}
if (string.IsNullOrEmpty(anonKey))
{
    throw new InvalidOperationException("SUPABASE_ANON_KEY environment variable is not set.");
}
if (string.IsNullOrEmpty(serviceRoleKey))
{
    throw new InvalidOperationException("SUPABASE_SERVICE_ROLE_KEY environment variable is not set.");
}

var options = new SupabaseOptions
{
    AutoRefreshToken = false,
    AutoConnectRealtime = true,
};
var anonClient = new Supabase.Client(supabaseUrl, anonKey, options);
builder.Services.AddSingleton(anonClient);

var adminClient = new Supabase.Client(supabaseUrl, serviceRoleKey, options);
builder.Services.AddSingleton(adminClient);

builder.Services.AddScoped<AkariApi.Services.SupabaseService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAkari", policyBuilder =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policyBuilder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
        else
        {
            policyBuilder.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrEmpty(origin)) return false;
                var uri = new Uri(origin);
                var host = uri.Host;
                return host == "akarimanga.dpdns.org" || host.EndsWith(".akarimanga.dpdns.org");
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
        }
    });
});

var app = builder.Build();

app.UseSwagger(options =>
{
    options.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
    {
        httpReq.HttpContext.Response.Headers["Cache-Control"] = "no-cache";
    });
});
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v2/swagger.json", "AkariApi v2");
});

app.UseCors("AllowAkari");

app.UseRateLimiter();

app.UseMiddleware<AkariApi.Middleware.TokenRefreshMiddleware>();
app.UseMiddleware<AkariApi.Middleware.MalTokenRefreshMiddleware>();

app.UseAuthorization();

app.MapControllers().RequireRateLimiting("fixed");

app.Run();
