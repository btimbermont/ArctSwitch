using System.Text.Json;
using NAudio.CoreAudioApi;

namespace ArctSwitch;

/// <summary>An endpoint reference. The ID is what Windows uses; the name is kept for display when the device is absent.</summary>
public sealed class DeviceRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public static DeviceRef? From(AudioEndpoint? e) => e == null ? null : new DeviceRef { Id = e.Id, Name = e.Name };
}

public sealed class DeviceProfile
{
    public DeviceRef? Playback { get; set; }
    public DeviceRef? Communications { get; set; }
    public DeviceRef? Recording { get; set; }
}

public sealed class Config
{
    public DeviceProfile HeadsetOn { get; set; } = new();
    public DeviceProfile HeadsetOff { get; set; } = new();
    public bool AutoSwitch { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public int PollIntervalMs { get; set; } = HeadsetMonitor.DefaultPollIntervalMs;

    static string FilePath => Path.Combine(Log.Dir, "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool Exists => File.Exists(FilePath);

    public static Config Load()
    {
        try
        {
            if (Exists) return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch (Exception ex) { Log.Write($"Config load failed: {ex.Message}"); }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>First-run guess: Arctis endpoints for ON, the current non-Arctis defaults for OFF.</summary>
    public static Config CreateDefault()
    {
        var render = AudioSwitcher.List(DataFlow.Render);
        var capture = AudioSwitcher.List(DataFlow.Capture);

        AudioEndpoint? Find(List<AudioEndpoint> list, string text) =>
            list.FirstOrDefault(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

        AudioEndpoint? NonArctisDefault(List<AudioEndpoint> list, DataFlow flow, Role role)
        {
            var id = AudioSwitcher.GetDefaultId(flow, role);
            var e = list.FirstOrDefault(x => x.Id == id);
            return e != null && !e.Name.Contains("Arctis", StringComparison.OrdinalIgnoreCase) ? e : null;
        }

        return new Config
        {
            HeadsetOn = new DeviceProfile
            {
                Playback = DeviceRef.From(Find(render, "Arctis 7 Game") ?? Find(render, "Arctis")),
                Communications = DeviceRef.From(Find(render, "Arctis 7 Chat") ?? Find(render, "Arctis")),
                Recording = DeviceRef.From(Find(capture, "Arctis")),
            },
            HeadsetOff = new DeviceProfile
            {
                Playback = DeviceRef.From(NonArctisDefault(render, DataFlow.Render, Role.Multimedia)),
                Communications = DeviceRef.From(NonArctisDefault(render, DataFlow.Render, Role.Communications)),
                Recording = DeviceRef.From(NonArctisDefault(capture, DataFlow.Capture, Role.Communications)),
            },
        };
    }
}
