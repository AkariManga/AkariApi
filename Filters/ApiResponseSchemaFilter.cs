using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AkariApi.Filters
{
    public class ApiResponseSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type == typeof(Models.ApiResponse))
            {
                if (schema.Properties.ContainsKey("result"))
                {
                    schema.Properties["result"] = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = new List<IOpenApiAny>
                        {
                            new OpenApiString("Success"),
                            new OpenApiString("Error")
                        }
                    };
                }
            }
            else if (context.Type.IsGenericType && context.Type.GetGenericTypeDefinition() == typeof(Models.SuccessResponse<>))
            {
                if (schema.Properties.ContainsKey("result"))
                {
                    schema.Properties["result"] = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = new List<IOpenApiAny>
                        {
                            new OpenApiString("Success")
                        }
                    };
                }
            }
            else if (context.Type == typeof(Models.ErrorResponse))
            {
                if (schema.Properties.ContainsKey("result"))
                {
                    schema.Properties["result"] = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = new List<IOpenApiAny>
                        {
                            new OpenApiString("Error")
                        }
                    };
                }
            }
        }
    }
}