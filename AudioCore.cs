using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace AudioRouteControl;

public sealed class AppSettings
{
    public List<RouteRule> Rules { get; set; } = new();
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
}

public sealed class RouteRule
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public float? Volume { get; set; }
    public bool? Muted { get; set; }
    public Dictionary<string, float> SessionVolumes { get; set; } = new();
    public Dictionary<string, bool> SessionMutes { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioRouteControl");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(settings, Options));
        File.Move(temporaryPath, FilePath, true);
    }
}

public sealed record AudioRefreshSnapshot(
    List<AudioOutput> Outputs,
    string? DefaultOutputId,
    List<AudioAppSession> Sessions);

public sealed class AudioApp : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, SessionHandle> _sessions = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public static AudioRefreshSnapshot Capture()
    {
        using var audio = new AudioApp();
        return new AudioRefreshSnapshot(
            audio.GetOutputs(),
            audio.GetDefaultOutputId(),
            audio.RefreshSessions());
    }

    public List<AudioOutput> GetOutputs()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var outputs = new List<AudioOutput>();
        foreach (var device in devices)
        {
            try { outputs.Add(new AudioOutput(device.ID, device.FriendlyName)); }
            finally { device.Dispose(); }
        }
        return outputs;
    }

    public string? GetDefaultOutputId()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch { return null; }
    }

    public List<AudioAppSession> RefreshSessions()
    {
        DisposeSessions();
        var result = new List<AudioAppSession>();
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var device in devices)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                var processOrdinals = new Dictionary<uint, int>();
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var keep = false;
                    try
                    {
                        var pid = (uint)session.GetProcessID;
                        if (pid == 0 || session.IsSystemSoundsSession) continue;
                        processOrdinals.TryGetValue(pid, out var ordinal);
                        processOrdinals[pid] = ordinal + 1;

                        Process? process = null;
                        try { process = Process.GetProcessById((int)pid); } catch { }
                        if (process is null) continue;

                        using (process)
                        {
                            var key = BuildSessionKey(pid, device.ID, ordinal);
                            var processName = process.ProcessName;
                            var persistenceKey = BuildPersistenceKey(processName, device.ID, ordinal);
                            var displayName = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                                ? processName
                                : process.MainWindowTitle;
                            var executablePath = TryGetPath(process);
                            var handle = new SessionHandle(session, pid, processName, displayName, executablePath, device.ID, device.FriendlyName);
                            _sessions[key] = handle;
                            result.Add(handle.ToSnapshot(key, persistenceKey));
                            keep = true;
                        }
                    }
                    catch { }
                    finally
                    {
                        if (!keep) session.Dispose();
                    }
                }
            }
            catch { }
            finally
            {
                device.Dispose();
            }
        }

        var ordered = result.OrderBy(x => x.DisplayName).ThenBy(x => x.ProcessName).ThenBy(x => x.Key).ToList();
        foreach (var group in ordered.GroupBy(x => (x.ProcessName, x.DisplayName), StringTupleComparer.OrdinalIgnoreCase))
        {
            var duplicateSessions = group.ToList();
            if (duplicateSessions.Count < 2) continue;
            for (var i = 0; i < duplicateSessions.Count; i++)
            {
                var session = duplicateSessions[i];
                var index = ordered.IndexOf(session);
                ordered[index] = session with { DisplayName = $"{session.DisplayName} · канал {i + 1}" };
            }
        }
        return ordered;
    }

    public void SetVolume(string key, float volume)
    {
        WithSession(key, session => session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1));
    }

    public async Task SetVolumeAsync(string key, float volume)
    {
        await RunOperationAsync(() => SetVolume(key, volume));
    }

    public void SetMute(string key, bool muted)
    {
        WithSession(key, session => session.SimpleAudioVolume.Mute = muted);
    }

    public async Task SetMuteAsync(string key, bool muted)
    {
        await RunOperationAsync(() => SetMute(key, muted));
    }

    public bool Route(string key, string deviceId)
    {
        return TryGetProcessId(key, out var processId)
            && PolicyConfig.SetPersistedDefaultAudioEndpoint(processId, deviceId);
    }

    public async Task<bool> RouteAsync(string key, string deviceId)
    {
        var result = false;
        await RunOperationAsync(() => result = Route(key, deviceId));
        return result;
    }

    private async Task RunOperationAsync(Action operation)
    {
        await _operationGate.WaitAsync();
        try
        {
            await Task.Run(operation);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void WithSession(string key, Action<AudioSessionControl> action)
    {
        if (!TryParseSessionKey(key, out var processId, out var deviceId, out var sessionIndex)) return;

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                if (deviceId is not null && !string.Equals(device.ID, deviceId, StringComparison.Ordinal)) continue;
                var sessions = device.AudioSessionManager.Sessions;
                var matchingOrdinal = 0;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var keep = false;
                    try
                    {
                        if ((uint)session.GetProcessID != processId || session.IsSystemSoundsSession) continue;
                        if (sessionIndex.HasValue && matchingOrdinal++ != sessionIndex.Value) continue;
                        action(session);
                        return;
                    }
                    catch { }
                    finally
                    {
                        if (!keep) session.Dispose();
                    }
                }
            }
            catch { }
            finally { device.Dispose(); }
        }
    }

    private static string BuildSessionKey(uint processId, string deviceId, int sessionIndex) =>
        $"{processId}|{deviceId}|{sessionIndex}";

    private static string BuildPersistenceKey(string processName, string deviceId, int sessionIndex) =>
        $"{processName}|{deviceId}|{sessionIndex}";

    private static bool TryParseSessionKey(string key, out uint processId, out string? deviceId, out int? sessionIndex)
    {
        processId = 0;
        deviceId = null;
        sessionIndex = null;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var firstSeparator = key.IndexOf('|');
        if (firstSeparator < 0)
            return uint.TryParse(key, out processId);

        var lastSeparator = key.LastIndexOf('|');
        if (lastSeparator <= firstSeparator
            || !uint.TryParse(key[..firstSeparator], out processId)
            || !int.TryParse(key[(lastSeparator + 1)..], out var parsedIndex))
            return false;

        deviceId = key[(firstSeparator + 1)..lastSeparator];
        sessionIndex = parsedIndex;
        return true;
    }

    private static bool TryGetProcessId(string key, out uint processId)
    {
        return TryParseSessionKey(key, out processId, out _, out _);
    }

    private static string TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; } catch { return ""; }
    }

    private void DisposeSessions()
    {
        foreach (var handle in _sessions.Values) handle.Dispose();
        _sessions.Clear();
    }

    public void Dispose()
    {
        DisposeSessions();
        _enumerator.Dispose();
        _operationGate.Dispose();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ProcessName, string DisplayName)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string ProcessName, string DisplayName) x, (string ProcessName, string DisplayName) y) =>
            string.Equals(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProcessName, string DisplayName) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProcessName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.DisplayName));
    }
}

public sealed class SessionHandle : IDisposable
{
    private readonly AudioSessionControl _session;

    public SessionHandle(AudioSessionControl session, uint processId, string processName, string displayName, string executablePath, string deviceId, string deviceName)
    {
        _session = session;
        ProcessId = processId;
        ProcessName = processName;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    public uint ProcessId { get; }
    public string ProcessName { get; }
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public AudioAppSession ToSnapshot(string key, string persistenceKey) =>
        new(key, ProcessId, ProcessName, DisplayName, ExecutablePath, DeviceId, DeviceName, _session.SimpleAudioVolume.Volume, _session.SimpleAudioVolume.Mute, persistenceKey);
    public void Dispose() => _session.Dispose();
}

public record AudioOutput(string Id, string Name);
public record AudioAppSession(
    string Key,
    uint ProcessId,
    string ProcessName,
    string DisplayName,
    string ExecutablePath,
    string DeviceId,
    string DeviceName,
    float Volume,
    bool Muted,
    string? PersistenceKey = null);

internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

internal static class PolicyConfig
{
    public static bool SetPersistedDefaultAudioEndpoint(uint processId, string deviceId)
    {
        if (processId == 0 || string.IsNullOrWhiteSpace(deviceId)) return false;

        IntPtr deviceIdHandle = IntPtr.Zero;
        object? factoryObject = null;
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            var hr = RoGetActivationFactory("Windows.Media.Internal.AudioPolicyConfig", ref iid, out factoryObject);
            if (hr < 0 || factoryObject is not IAudioPolicyConfigFactory factory) return false;

            hr = WindowsCreateString(deviceId, deviceId.Length, out deviceIdHandle);
            if (hr < 0) return false;

            return factory.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Console, deviceIdHandle) == 0;
        }
        catch { return false; }
        finally
        {
            if (deviceIdHandle != IntPtr.Zero) WindowsDeleteString(deviceIdHandle);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                try { Marshal.ReleaseComObject(factoryObject); } catch { }
            }
        }
    }

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        int AddContextVolumeChange();
        int RemoveContextVolumeChanged();
        int AddRingerVibrateStateChanged();
        int RemoveRingerVibrateStateChanged();
        int SetVolumeGroupGainForId();
        int GetVolumeGroupGainForId();
        int GetActiveVolumeGroupForEndpointId();
        int GetVolumeGroupsForEndpoint();
        int GetCurrentVolumeContext();
        int SetVolumeGroupMuteForId();
        int GetVolumeGroupMuteForId();
        int SetRingerVibrateState();
        int GetRingerVibrateState();
        int SetPreferredChatApplication();
        int ResetPreferredChatApplication();
        int GetPreferredChatApplication();
        int GetCurrentChatApplications();
        int AddChatContextChanged();
        int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
        int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hString);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hString);
}
