using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AkariApi.Filters
{
    public class RemoveExtraContentTypesOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.RequestBody?.Content != null)
            {
                // Keep only application/json, remove others like text/json and application/*+json
                var keysToRemove = operation.RequestBody.Content.Keys
                    .Where(key => key != "application/json")
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    operation.RequestBody.Content.Remove(key);
                }
            }
        }
    }
}