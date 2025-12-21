using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using AkariApi.Attributes;

namespace AkariApi.Filters
{
    public class AuthorizeCheckOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var hasRequireAttribute = context.MethodInfo.GetCustomAttributes(true).Any(attr => attr is RequireTokenRefreshAttribute) ||
                                      context.MethodInfo.DeclaringType?.GetCustomAttributes(true).Any(attr => attr is RequireTokenRefreshAttribute) == true;

            var hasOptionalAttribute = context.MethodInfo.GetCustomAttributes(true).Any(attr => attr is OptionalTokenRefreshAttribute) ||
                                       context.MethodInfo.DeclaringType?.GetCustomAttributes(true).Any(attr => attr is OptionalTokenRefreshAttribute) == true;

            if (hasRequireAttribute || hasOptionalAttribute)
            {
                operation.Security = new List<OpenApiSecurityRequirement>
                {
                    new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer Token"
                                }
                            },
                            new string[] {}
                        }
                    }
                };
            }
            if (hasOptionalAttribute)
            {
                operation.Description += "\n*Authentication is optional for this endpoint.*";
            }
        }
    }
}