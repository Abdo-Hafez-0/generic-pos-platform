using System.Net;
using System.Text;
using Fiscal.Contracts.Models;
using Xunit.Abstractions;

namespace Fiscal.Tests;

/// <summary>Runs only with GENERICPOS_LOCAL_ZATCA=&lt;port&gt;: a tool, not a check.</summary>
public sealed class LocalZatcaFactAttribute : FactAttribute
{
    public LocalZatcaFactAttribute()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("GENERICPOS_LOCAL_ZATCA"), out _))
            Skip = "Serves the stand-in ZATCA on this computer for a check of the real application: set GENERICPOS_LOCAL_ZATCA=<port> to run it.";
    }
}

/// <summary>
/// MISS-11d-4: the stand-in ZATCA (<see cref="FakeZatca"/>: onboarding, compliance checks, reporting) served over plain HTTP on 127.0.0.1, so the
/// real application can be checked end to end through its real transport without anything leaving the computer (the transport allows plain
/// HTTP only to this computer). Serves for GENERICPOS_LOCAL_ZATCA_MINUTES (default 10) and logs each call. Invoices whose number is in
/// GENERICPOS_LOCAL_ZATCA_REFUSE (comma separated) are refused.
/// </summary>
public sealed class LocalZatcaServer(ITestOutputHelper output)
{
    [LocalZatcaFact]
    public async Task Serve()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable("GENERICPOS_LOCAL_ZATCA")!);
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("GENERICPOS_LOCAL_ZATCA_MINUTES"), out var m) ? m : 10;
        var log = Environment.GetEnvironmentVariable("GENERICPOS_LOCAL_ZATCA_LOG");
        var zatca = new FakeZatca();
        foreach (var number in (Environment.GetEnvironmentVariable("GENERICPOS_LOCAL_ZATCA_REFUSE") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            zatca.RefuseReport[number.Trim()] = "[BR-KSA-31] refused by the local stand-in for this check";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        void Log(string line)
        {
            output.WriteLine(line);
            if (log is not null) File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
        }

        Log($"serving the stand-in ZATCA on http://127.0.0.1:{port}/ for {minutes} minutes");
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
            catch (OperationCanceledException) { break; }

            var request = context.Request;
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => request.Headers[k]!);
            // the stand-in checks the path's ending: a /simulation/ or /core/ prefix is kept
            var answer = await zatca.SendAsync(new FiscalHttpRequest(request.HttpMethod, "https://gw-fatoora.zatca.gov.sa" + request.Url!.AbsolutePath, headers,
                body.Length == 0 ? null : body));
            Log($"{request.HttpMethod} {request.Url!.AbsolutePath} -> {answer.StatusCode}");
            context.Response.StatusCode = answer.StatusCode == 0 ? 502 : answer.StatusCode;
            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        Log($"stopped; reported: {string.Join(", ", zatca.Reported)}");
    }
}
