using Supabase;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.Mvc;
using AkariApi.Filters;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using Analytics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();

// Response compression (gzip)
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<GzipCompressionProvider>();
    options.EnableForHttps = true;
    // compress common API response types
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});

// configure gzip level
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v2", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "AkariApi v2", Version = "v2" });
    options.SchemaFilter<EnumSchemaFilter>();
    options.SchemaFilter<ApiResponseSchemaFilter>();
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "AkariApi.xml"));
    if (builder.Environment.IsDevelopment())
    {
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "http://localhost:5188" });
    }
    else
    {
        options.AddServer(new Microsoft.OpenApi.Models.OpenApiServer { Url = "https://api.akarimanga.dpdns.org" });
    }
    options.AddSecurityDefinition("Bearer Token", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "Authorization",
    });
    options.AddSecurityDefinition("MalCookieAuth", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Cookie,
        Name = "mal_access_token",
    });
    options.AddSecurityDefinition("AniCookieAuth", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Cookie,
        Name = "ani_access_token",
    });
    options.OperationFilter<MalAuthorizeCheckOperationFilter>();
    options.OperationFilter<RemoveExtraContentTypesOperationFilter>();
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

var apiKey = builder.Configuration["API_KEY"] ?? Environment.GetEnvironmentVariable("API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException("API_KEY environment variable is not set.");
}

builder.Services.AddRateLimiter(_ => _
    .AddFixedWindowLimiter(policyName: "fixed", options =>
    {
        options.PermitLimit = 20;
        options.Window = TimeSpan.FromSeconds(5);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 10;
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
    AutoConnectRealtime = false,
};
var anonClient = new Supabase.Client(supabaseUrl, anonKey, options);
builder.Services.AddSingleton(anonClient);

var adminClient = new Supabase.Client(supabaseUrl, serviceRoleKey, options);
builder.Services.AddSingleton(adminClient);

builder.Services.AddScoped<AkariApi.Services.SupabaseService>();
builder.Services.AddScoped<AkariApi.Services.PostgresService>();
builder.Services.AddScoped<AkariApi.Services.NotificationService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAkari", policyBuilder =>
    {
        policyBuilder.SetIsOriginAllowed(origin => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseSwagger(options =>
{
    options.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
    {
        var response = httpReq.HttpContext.Response;

        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";

        // Remove conditional headers
        response.Headers.Remove("Last-Modified");
        response.Headers.Remove("ETag");
    });
});
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v2/swagger.json", "AkariApi v2");
});

app.UseCors("AllowAkari");

// enable gzip compression
app.UseResponseCompression();


bool IsApiKeyValid(HttpContext context)
{
    var key = context.Request.Headers["X-API-Key"];
    return !string.IsNullOrEmpty(key) && key == apiKey;
}
app.UseWhen(context => !IsApiKeyValid(context), app => app.UseRateLimiter());

app.UseMiddleware<AkariApi.Middleware.MalTokenRefreshMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<AkariApi.Middleware.RequestTimingMiddleware>();
} else {
    app.UseAnalytics();
}

app.UseAuthorization();

app.MapControllers().RequireRateLimiting("fixed");

app.Run();
