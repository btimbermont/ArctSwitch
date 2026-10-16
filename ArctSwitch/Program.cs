using System.Runtime.InteropServices;

namespace ArctSwitch;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            if (!AttachConsole(-1)) AllocConsole();
            int secs = 30;
            var i = Array.FindIndex(args, a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var s)) secs = s;
            HeadsetMonitor.Probe(Console.Out, secs);
            return 0;
        }

        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            if (!AttachConsole(-1)) AllocConsole();
            ListDevices();
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\ArctSwitch.SingleInstance", out bool first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>Diagnostic: prints endpoints and current defaults, then re-applies the current defaults (a no-op) to verify switching works.</summary>
    static void ListDevices()
    {
        foreach (var flow in new[] { NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DataFlow.Capture })
        {
            var mm = AudioSwitcher.GetDefaultId(flow, NAudio.CoreAudioApi.Role.Multimedia);
            var comm = AudioSwitcher.GetDefaultId(flow, NAudio.CoreAudioApi.Role.Communications);
            Console.WriteLine($"{flow}:");
            foreach (var d in AudioSwitcher.List(flow))
                Console.WriteLine($"  {(d.Id == mm ? "D" : " ")}{(d.Id == comm ? "C" : " ")} {d.Name}");
        }

        var current = new DeviceProfile
        {
            Playback = Ref(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia),
            Communications = Ref(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Communications),
            Recording = Ref(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications),
        };
        // Recording is applied to all roles, so only re-apply it when that is genuinely unchanged.
        if (AudioSwitcher.GetDefaultId(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Multimedia) != current.Recording?.Id)
            current.Recording = null;
        var failed = AudioSwitcher.Apply(current);
        Console.WriteLine(failed.Count == 0 ? "Re-applying current defaults: OK" : "Re-apply errors: " + string.Join("; ", failed));

        static DeviceRef? Ref(NAudio.CoreAudioApi.DataFlow f, NAudio.CoreAudioApi.Role r)
        {
            var id = AudioSwitcher.GetDefaultId(f, r);
            return id == null ? null : new DeviceRef { Id = id, Name = id };
        }
    }
}
