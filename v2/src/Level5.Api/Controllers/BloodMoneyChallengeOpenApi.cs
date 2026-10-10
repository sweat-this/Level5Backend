using System.Text.Json.Nodes;
using Level5.Domain.BloodMoney;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Level5.Api.Controllers;

public sealed class BloodMoneyChallengeSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if ((context.Type != typeof(BloodMoneyChallengeStatus) && context.Type != typeof(BloodMoneyParticipantStatus)) ||
            schema is not OpenApiSchema concrete) return;
        concrete.Type = JsonSchemaType.String;
        concrete.Format = null;
        concrete.Enum = Enum.GetNames(context.Type).Select(name => (JsonNode)JsonValue.Create(name)!).ToList();
    }
}
