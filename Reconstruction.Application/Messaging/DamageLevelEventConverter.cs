using System.Text.Json;
using System.Text.Json.Serialization;
using Reconstruction.Domain;
namespace Reconstruction.Application.Messaging;

internal sealed class DamageLevelEventConverter : JsonConverter<DamageLevel>
{
    public override DamageLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((DamageLevel)number))
            return (DamageLevel)number;
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<DamageLevel>(value, out var damage) && Enum.IsDefined(damage)) return damage;
            return value switch { "Nivel_1" => DamageLevel.Minor, "Nivel_2" => DamageLevel.Moderate,
                "Nivel_3" => DamageLevel.Severe, "Nivel_4" => DamageLevel.Destroyed, _ => throw new JsonException("Categoría histórica inválida.") };
        }
        throw new JsonException("Categoría de daño inválida.");
    }
    public override void Write(Utf8JsonWriter writer, DamageLevel value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
