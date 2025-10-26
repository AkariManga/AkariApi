using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using AkariApi.Attributes;

namespace AkariApi.Filters
{
    public class MalAuthorizeCheckOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var hasRequireMalTokenRefresh = context.MethodInfo.GetCustomAttributes(true).OfType<RequireMalTokenRefreshAttribute>().Any() ||
                                             context.MethodInfo.DeclaringType!.GetCustomAttributes(true).OfType<RequireMalTokenRefreshAttribute>().Any();

            if (hasRequireMalTokenRefresh)
            {
                operation.Security ??= new List<OpenApiSecurityRequirement>();
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "MalCookieAuth"
                            }
                        },
                        new List<string>()
                    }
                });
            }
        }
    }
}