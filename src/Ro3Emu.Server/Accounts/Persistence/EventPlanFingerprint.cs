using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Bind replay to the detached command values, never to caller-side apply callbacks.
/// Only concrete plan fields are included: writer revision expectations are checked separately.
/// Field names and types intentionally make incompatible command versions refuse an old replay.
/// </summary>
internal static class EventPlanFingerprint
{
    internal static string Capture(RoleItemPlan plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            Write(writer, plan.Guards, 0);
            Write(writer, plan.Parts, 0);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 32) throw new InvalidDataException("Event plan value graph is too deep.");
        if (value is null) { writer.WriteNullValue(); return; }
        var type = value.GetType();
        if (value is string text) { writer.WriteStringValue(text); return; }
        if (value is byte[] bytes) { writer.WriteBase64StringValue(bytes); return; }
        if (type.IsPrimitive || type.IsEnum || value is decimal or Guid or DateTime or DateTimeOffset)
        {
            JsonSerializer.Serialize(writer, value, type);
            return;
        }
        if (value is Delegate || value is IDisposable || value is Type)
            throw new InvalidDataException("Event plans may only capture detached values: " + type.FullName);
        if (value is IDictionary dictionary)
        {
            var entries = new List<DictionaryEntry>();
            var cursor = dictionary.GetEnumerator();
            while (cursor.MoveNext()) entries.Add(cursor.Entry);
            writer.WriteStartArray();
            foreach (DictionaryEntry entry in entries
                         .OrderBy(entry => JsonSerializer.Serialize(entry.Key, entry.Key.GetType()), StringComparer.Ordinal))
            {
                writer.WriteStartArray(); Write(writer, entry.Key, depth + 1); Write(writer, entry.Value, depth + 1); writer.WriteEndArray();
            }
            writer.WriteEndArray();
            return;
        }
        if (value is IEnumerable sequence)
        {
            writer.WriteStartArray();
            foreach (var item in sequence) Write(writer, item, depth + 1);
            writer.WriteEndArray();
            return;
        }
        if (type.Assembly != typeof(DbPlan).Assembly && !type.IsValueType)
            throw new InvalidDataException("Unsupported detached event value: " + type.FullName);
        writer.WriteStartObject();
        writer.WriteString("type", type.FullName);
        var fields = new List<FieldInfo>();
        for (Type? current = type; current is not null && current != typeof(DbPlan) && current != typeof(object); current = current.BaseType)
            fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        foreach (var field in fields.OrderBy(field => field.DeclaringType!.FullName + "/" + field.Name, StringComparer.Ordinal))
        {
            // Guard plans retain revision captures for admission, not as business command values.
            // A fresh capture of an unchanged guarded blob must replay after an unrelated part advances.
            if (field.FieldType == typeof(RoleRevisionVector) || field.FieldType == typeof(OwnerRevisionValue)
                || field.FieldType == typeof(OwnerRevisionValue?)) continue;
            writer.WritePropertyName(field.DeclaringType!.FullName + "/" + field.Name);
            Write(writer, field.GetValue(value), depth + 1);
        }
        writer.WriteEndObject();
    }
}
