using System.Drawing;
using System.IO;
using System.Text.Json;

namespace ClaudeZhInput;

public static class ToolbarPositionStore
{
    public static Point Load(string path, Point fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("X", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var left) &&
                root.TryGetProperty("Y", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var top))
                return new Point(left, top);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return fallback;
    }

    public static void Save(string path, Point position)
    {
        // Explicit JSON names keep the on-disk format independent of obfuscation.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("X", position.X);
            writer.WriteNumber("Y", position.Y);
            writer.WriteEndObject();
        }
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, true);
    }
}
