using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ArctSwitch;

public sealed record AudioEndpoint(string Id, string Name);

/// <summary>Lists audio endpoints and sets Windows default devices via the (undocumented, long-stable) IPolicyConfig COM interface.</summary>
public static class AudioSwitcher
{
    public static List<AudioEndpoint> List(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        var result = new List<AudioEndpoint>();
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (d) result.Add(new AudioEndpoint(d.ID, d.FriendlyName));
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string? GetDefaultId(DataFlow flow, Role role)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(flow, role)) return null;
            using var d = en.GetDefaultAudioEndpoint(flow, role);
            return d.ID;
        }
        catch { return null; }
    }

    public static bool IsActive(string id)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDevice(id);
            return d.State == DeviceState.Active;
        }
        catch { return false; }
    }

    /// <summary>Applies a profile. Returns a description of each setting that could not be applied.</summary>
    public static List<string> Apply(DeviceProfile p)
    {
        var failed = new List<string>();
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            void Set(DeviceRef? dev, string label, params ERole[] roles)
            {
                if (dev == null || string.IsNullOrEmpty(dev.Id)) return; // "leave unchanged"
                if (!IsActive(dev.Id)) { failed.Add($"{label}: {dev.Name} (not available)"); return; }
                foreach (var r in roles)
                {
                    int hr = policy.SetDefaultEndpoint(dev.Id, r);
                    if (hr != 0) { failed.Add($"{label}: {dev.Name} (0x{hr:X8})"); return; }
                }
            }

            Set(p.Playback, "Playback", ERole.Console, ERole.Multimedia);
            Set(p.Communications, "Communications", ERole.Communications);
            Set(p.Recording, "Recording", ERole.Console, ERole.Multimedia, ERole.Communications);
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
        return failed;
    }

    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}
