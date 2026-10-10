using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Level5.Api.Controllers;

public sealed class BloodMoneyChatSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if ((context.Type != typeof(BloodMoneyChatReportReason) && context.Type != typeof(BloodMoneyChatVisibility)) ||
            schema is not OpenApiSchema concrete) return;
        concrete.Type = JsonSchemaType.String;
        concrete.Format = null;
        concrete.Enum = Enum.GetNames(context.Type).Select(name => (JsonNode)JsonValue.Create(name)!).ToList();
    }
}

public sealed class BloodMoneyChatOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(BloodMoneyChatController)) return;
        var limit = operation.Parameters?.FirstOrDefault(parameter => parameter.Name == "limit");
        if (limit is OpenApiParameter limitParameter)
            limitParameter.Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Maximum = "100", Default = JsonValue.Create(50) };
        var cursor = operation.Parameters?.FirstOrDefault(parameter => parameter.Name == "cursor");
        if (cursor?.Schema is OpenApiSchema cursorSchema) cursorSchema.MaxLength = 1024;
    }
}
