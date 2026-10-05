using System.Text.Json;
namespace CS2Votes;

// data that must survive a map change
internal sealed class PersistentState
{
    public ulong PendingId { get; set; }
    public List<ulong> History { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PersistentState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PersistentState>(File.ReadAllText(path)) ?? new();
        }
        // bad file only loses the history.
        catch
        {
        }
        return new();
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, true);
}   }