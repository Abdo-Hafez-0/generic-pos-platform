using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace EndToEnd.Tests;

/// <summary>Runs only when GENERICPOS_E2E=1: it starts real executables and drives a real window for a few minutes.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GENERICPOS_E2E") != "1")
            Skip = "End-to-end test on the real executable: set GENERICPOS_E2E=1 to run it (MISS-06b).";
        Timeout = 0;
    }
}

internal static class Repo
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    /// <summary>Debug or Release, as this test was built.</summary>
    public static string Configuration => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";

    public static int FreePort()
    {
        var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        for (var port = 55400; port < 55500; port++)
            if (!used.Contains(port)) return port;
        throw new InvalidOperationException("No free port.");
    }
}

/// <summary>
/// The REAL license server (LicenseServer.Api), run from a COPY of its build output (running it from bin would lock files the solution
/// build needs), in the Development environment with a throwaway signing key and one development license for all desktop modules.
/// </summary>
internal sealed class LicenseServerProcess : IDisposable
{
    public const string KeyId = "e2e-key";
    public static readonly string[] Modules =
        ["catalog", "inventory", "sales", "pos", "customers", "suppliers", "purchasing", "pricing", "payments", "users", "audit", "cash-management", "reporting", "cloud-backup"];

    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public LicenseServerProcess(string folder)
    {
        Folder = folder;
        var source = Path.Combine(Repo.Root, "src", "Cloud", "LicenseServer", "LicenseServer.Api", "bin", Repo.Configuration, "net10.0");
        var copy = Path.Combine(folder, "license-server");
        CopyDirectory(source, copy);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = Path.Combine(folder, "license-signing.pem");
        File.WriteAllText(pem, key.ExportPkcs8PrivateKeyPem());
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        Port = Repo.FreePort();
        ActivationKey = "E2EAB-CDEFG-HJKLM-NPQRS-TUVWX";
        var args = new List<string>
        {
            "--environment", "Development", "--urls", $"http://127.0.0.1:{Port}",
            $"--LicenseServer:SigningKeyPemPath={pem}", $"--LicenseServer:KeyId={KeyId}",
            $"--LicenseServer:DevLicenses:0:ActivationKey={ActivationKey}", "--LicenseServer:DevLicenses:0:CustomerId=E2E-SHOP",
            "--LicenseServer:DevLicenses:0:ProductId=genericpos",
            $"--LicenseServer:DevLicenses:0:ValidFrom={DateTimeOffset.UtcNow.AddDays(-1):O}", $"--LicenseServer:DevLicenses:0:ValidUntil={DateTimeOffset.UtcNow.AddDays(30):O}",
            "--LicenseServer:DevLicenses:0:Features:0=basic",
        };
        for (var i = 0; i < Modules.Length; i++) args.Add($"--LicenseServer:DevLicenses:0:Modules:{i}={Modules[i]}");

        var start = new ProcessStartInfo(Path.Combine(copy, "LicenseServer.Api.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = copy };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                if (http.GetAsync($"{BaseUrl}health").GetAwaiter().GetResult().IsSuccessStatusCode) break;
            }
            catch (HttpRequestException)
            {
            }

            if (_process.HasExited || DateTime.UtcNow > deadline)
                throw new InvalidOperationException("The license server did not start:\n" + Output);
            Thread.Sleep(250);
        }
    }

    public string Folder { get; }
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public string PublicKey { get; }
    public string ActivationKey { get; }

    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) throw new InvalidOperationException($"Build LicenseServer.Api first: {source} does not exist.");
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }
}

/// <summary>Reads what the ESC/POS file printer received: the text lines of each receipt (commands removed) and whether paper was cut.</summary>
internal static class EscPosReader
{
    public static (IReadOnlyList<string> Lines, int Cuts) Read(byte[] bytes)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var cuts = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0x1B && i + 1 < bytes.Length)
            {
                i += bytes[i + 1] switch
                {
                    0x40 => 1,   // ESC @: initialise
                    0x70 => 4,   // ESC p m t1 t2: drawer pulse
                    _ => 2,      // ESC a n / ESC E n / ESC ! n
                };
                continue;
            }
            if (b == 0x1D && i + 1 < bytes.Length && bytes[i + 1] == 0x56) { cuts++; i += 3; continue; }   // GS V B n: cut
            if (b == 0x0A) { lines.Add(current.ToString()); current.Clear(); continue; }
            if (b >= 0x20 && b < 0x7F) current.Append((char)b);
        }

        if (current.Length > 0) lines.Add(current.ToString());
        return (lines, cuts);
    }
}
