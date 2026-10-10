using System.Text.Json;
using System.Text.Json.Serialization;
using Level5.Application.BloodMoney;

namespace Level5.Api.Controllers;

/// <summary>Only the six named wire values are valid. Preserve invalid input as an undefined
/// value so Application/persistence can reject it after canonical resource authorization.</summary>
public sealed class BloodMoneyChatReportReasonConverter : JsonConverter<BloodMoneyChatReportReason>
{
    public override BloodMoneyChatReportReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            foreach (var reason in Enum.GetValues<BloodMoneyChatReportReason>())
                if (reason.ToString() == text) return reason;
        }
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            using (JsonDocument.ParseValue(ref reader)) { }
        return (BloodMoneyChatReportReason)(-1);
    }

    public override void Write(Utf8JsonWriter writer, BloodMoneyChatReportReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
