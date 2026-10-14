using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Platform.Infrastructure.Persistence;

namespace Client.Host.Configuration;

/// <summary>
/// MISS-07b: settings chosen on this installation's screens (for example the devices), kept next to the database in <c>local-settings.json</c>.
/// The file is read into the configuration after appsettings.json and before environment variables, so a choice made in the application
/// replaces the installer's default and a technician's environment override still wins. Each owner writes only its own top-level section;
/// the rest of the file is kept as it is.
/// </summary>
public sealed class LocalSettingsFile(string path)
{
    public const string FileName = "local-settings.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };
    private static readonly object Lock = new();

    public string Path { get; } = path;

    /// <summary>The file for the database the configuration points at (the same folder as the database).</summary>
    public static LocalSettingsFile For(IConfiguration configuration)
    {
        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
        return new LocalSettingsFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.ResolveDatabasePath())!, FileName));
    }

    /// <summary>Replaces one top-level section (written as a new file, then moved over the old one).</summary>
    public void WriteSection<T>(string section, T value)
    {
        lock (Lock)
        {
            var root = Read() ?? new JsonObject();
            root[section] = JsonSerializer.SerializeToNode(value, Json);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>One section as it is in the file, or default when there is none (or the file cannot be read).</summary>
    public T? ReadSection<T>(string section)
    {
        lock (Lock)
        {
            var node = Read()?[section];
            return node is null ? default : node.Deserialize<T>(Json);
        }
    }

    private JsonObject? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
