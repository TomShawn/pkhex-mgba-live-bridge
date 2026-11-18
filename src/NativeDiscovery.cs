using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

namespace PKHeX.GbaBridge;

internal static class NativeDiscovery
{
    [DllImport("libSystem.B.dylib",EntryPoint = "realpath")]
    private static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] result);

    internal static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS()) return full;
        byte[] result = new byte[4096];
        if (RealPath(full,result) == 0) return full;
        return Encoding.UTF8.GetString(result.AsSpan(0,Array.IndexOf(result,(byte)0)));
    }

    internal static List<BridgeEndpoint> List(string stateDirectory, string? savePath = null)
    {
        string directory = Path.Combine(stateDirectory,"sessions");
        if (!Directory.Exists(directory)) return [];
        var matches = new List<BridgeEndpoint>();
        foreach (string path in Directory.EnumerateFiles(directory,"*.json"))
        {
            try
            {
                var endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllBytes(path));
                if (endpoint is not { Protocol: 3, SocketPath: not null, InstanceId: not null }
                    || savePath is not null && CanonicalPath(endpoint.SavePath) != CanonicalPath(savePath)) continue;
                using var client = new EmulatorClient(endpoint,1000);
                client.Ping();
                matches.Add(endpoint);
            }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException
                or JsonException or TimeoutException or InvalidOperationException or ArgumentException) { }
        }
        return matches;
    }

    internal static BridgeEndpoint? Find(string stateDirectory, string savePath)
    {
        var matches = List(stateDirectory,savePath);
        if (matches.Count > 1)
            throw new BridgeException("多个模拟器正在使用同一份存档，请选择并保留一个活动游戏会话。");
        return matches.SingleOrDefault();
    }
}
