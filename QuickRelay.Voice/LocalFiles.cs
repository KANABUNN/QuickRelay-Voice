using System.Text.Json;

namespace QuickRelay.Voice;

internal static class LocalFiles
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickRelay Voice");
    private static readonly JsonSerializerOptions Serializer = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T? Read<T>(string file, long maxBytes)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(Folder, file));
            if (stream.Length > maxBytes)
                throw new InvalidDataException("保存済みファイルが上限を超えています。");
            return JsonSerializer.Deserialize<T>(stream, Serializer);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }

    public static void Write<T>(string file, T value, long maxBytes = 8 * 1024 * 1024)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, file);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Serializer);
                if (stream.Length > maxBytes)
                    throw new InvalidDataException("保存ファイルが上限を超えています。");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
