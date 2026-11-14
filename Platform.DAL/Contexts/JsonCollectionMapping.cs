using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Platform.DAL.Contexts;

internal static class JsonCollectionMapping
{
    private static string Serialize<T>(IReadOnlyList<T> value) => JsonSerializer.Serialize(value);
    private static IReadOnlyList<T> Deserialize<T>(string value) =>
        (JsonSerializer.Deserialize<T[]>(value) ?? []).ToList().AsReadOnly();
    public static PropertyBuilder<IReadOnlyList<T>> HasJsonCollection<T>(this PropertyBuilder<IReadOnlyList<T>> property, string column)
    {
        property.HasColumnName(column).HasColumnType("TEXT").HasDefaultValueSql("'[]'")
            .HasConversion(new ValueConverter<IReadOnlyList<T>, string>(v => Serialize(v), v => Deserialize<T>(v)),
                new ValueComparer<IReadOnlyList<T>>((a, b) => Serialize(a!) == Serialize(b!),
                    v => Serialize(v).GetHashCode(), v => Deserialize<T>(Serialize(v))));
        return property;
    }
}
