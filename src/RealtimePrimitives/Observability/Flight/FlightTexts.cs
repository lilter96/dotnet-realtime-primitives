using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// What leaves the laptop. A text field of an event (<see cref="FlightEvent.Text"/>: a transcript, a question, an answer, a refused paragraph,
/// the "lost" / "instead" of a shrink) is recognisable by its sibling <c>{name}_len</c>; an event sent to a sink outside the machine's own
/// files (Loki, a Redis stream) goes without those texts unless <c>Flight:TextToExternalSinks</c> says otherwise, and keeps every length.
/// The local file keeps everything: it is where the interview is analysed.
/// </summary>
public static class FlightTexts
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The event's JSON without its text fields (lengths kept). An event that is not a JSON object comes back as an empty object.</summary>
    public static string WithoutTexts(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "{}";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) names.Add(property.Name);
            using var buffer = new MemoryStream(json.Length);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && names.Contains(property.Name + "_len")) continue;
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
