using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OutfitStudio.Core.Protocol;

namespace OutfitStudio.Plugin;

internal sealed class WorkerClient(string pluginDirectory, string configDirectory)
{
    public string BundledPath => Path.Combine(pluginDirectory, "worker", "OutfitStudio.Worker.exe");

    public async Task<WorkerResponse> RunAsync(WorkerRequest request, string workerOverride, string dotnetPath,
        Action<WorkerProgress> progress, CancellationToken cancellation)
    {
        var executable = string.IsNullOrWhiteSpace(workerOverride) ? BundledPath : Path.GetFullPath(workerOverride);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The conversion worker is missing. Install the complete Outfit Studio bundle or set its path in Advanced.", executable);
        var jobDirectory = Path.Combine(configDirectory, "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        var requestPath = Path.Combine(jobDirectory, "request.json");
        var responsePath = Path.Combine(jobDirectory, "response.json");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, WorkerProtocol.Json), cancellation);
            var useDotnet = Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = useDotnet ? dotnetPath : executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            if (useDotnet) start.ArgumentList.Add(executable);
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("--response"); start.ArgumentList.Add(responsePath);
            cancellation.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the conversion worker.");
            var cancel = MonitorCancellationAsync(process, cancellation);
            var stderr = ReadErrorsAsync(process.StandardError);
            var stdout = ReadProgressAsync(process.StandardOutput, progress);
            await process.WaitForExitAsync(CancellationToken.None);
            await cancel;
            await stdout;
            var errors = await stderr;
            if (!File.Exists(responsePath))
            {
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"The worker exited without a result (exit {process.ExitCode}). {errors}");
            }
            // Publication may win a race with cancellation. Preserve a successful response so the
            // UI can expose its output path; the cancelled token still prevents registration/enable.
            var result = JsonSerializer.Deserialize<WorkerResponse>(await File.ReadAllTextAsync(responsePath), WorkerProtocol.Json)
                ?? throw new InvalidDataException("The worker returned an empty response.");
            if (result.ProtocolVersion != WorkerProtocol.Version)
                throw new InvalidOperationException("Plugin and worker versions do not match. Install the complete bundle together.");
            if (!result.Success || process.ExitCode != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException(result.Error ?? $"Conversion failed (exit {process.ExitCode}). {errors}");
            }
            if (request.Mode == ConversionMode.DestinationSizes && result.AutomaticPlan is null)
                throw new InvalidOperationException("This worker does not support destination sizes. Install the complete updated bundle, including the worker folder.");
            if (request.Operation != WorkerOperation.Convert || string.IsNullOrWhiteSpace(result.OutputDirectory))
                cancellation.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (process is not null) { Kill(process); process.Dispose(); }
            // Only this randomly named job directory is removed; converted output is managed by the worker.
            try { Directory.Delete(jobDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task MonitorCancellationAsync(Process process, CancellationToken cancellation)
    {
        try { await process.WaitForExitAsync(cancellation); return; }
        catch (OperationCanceledException) { }
        try
        {
            // Let the worker remove its unpublished staging directory before forced termination.
            await process.StandardInput.WriteLineAsync("cancel");
            await process.StandardInput.FlushAsync();
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        var exit = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))) != exit) Kill(process);
        await exit;
    }

    private static async Task ReadProgressAsync(StreamReader reader, Action<WorkerProgress> progress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 32768) continue;
            try
            {
                if (JsonSerializer.Deserialize<WorkerProgress>(line, WorkerProtocol.Json) is { } value)
                    progress(value);
            }
            catch (JsonException) { /* Workers may write diagnostic lines in addition to protocol progress. */ }
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var summary = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (summary.Length < 8192) summary.Append(buffer, 0, Math.Min(count, 8192 - summary.Length));
        return summary.ToString().Trim();
    }
}
