using System.Text.Json;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Core.Services;

// The worker deliberately has no Dalamud, game process, or native geometry dependencies.
return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args is ["--help"] or ["-h"] || args.Length == 0)
    {
        Console.WriteLine("Outfit Studio worker\nUsage: OutfitStudio.Worker --request request.json --response response.json");
        return args.Length == 0 ? 2 : 0;
    }

    if (args.Length != 4 || args[0] != "--request" || args[2] != "--response")
    {
        Console.Error.WriteLine("Expected --request <path> --response <path>. Use --help for usage.");
        return 2;
    }

    string requestPath = Path.GetFullPath(args[1]);
    string responsePath = Path.GetFullPath(args[3]);
    if (string.Equals(requestPath, responsePath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Request and response paths must differ.");
        return 2;
    }
    if (File.Exists(responsePath) || Directory.Exists(responsePath))
    {
        Console.Error.WriteLine("Response path already exists; choose a new response filename.");
        return 2;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    // A line on stdin requests cooperative cancellation, including cleanup of staged output.
    _ = Task.Run(async () =>
    {
        try
        {
            if (string.Equals(await Console.In.ReadLineAsync(), "cancel", StringComparison.Ordinal))
                cancellation.Cancel();
        }
        catch (IOException) { }
    });

    WorkerResponse response;
    try
    {
        var info = new FileInfo(requestPath);
        if (info.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Request is too large (maximum 4 MiB).");
        var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(requestPath, cancellation.Token), WorkerProtocol.Json)
            ?? throw new InvalidDataException("Request JSON is empty.");
        var compactJson = new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = false };
        var progress = new SynchronousProgress(p => Console.WriteLine(JsonSerializer.Serialize(p, compactJson)));
        response = await ConversionService.ExecuteAsync(request, progress, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        response = new WorkerResponse { Error = "Conversion cancelled." };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        response = new WorkerResponse { Error = exception.Message };
    }

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(responsePath)!);
        var temporary = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(response, WorkerProtocol.Json));
            File.Move(temporary, responsePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Could not write response: {exception.Message}");
        return 3;
    }
    return response.Success ? 0 : 1;
}

sealed class SynchronousProgress(Action<WorkerProgress> handler) : IProgress<WorkerProgress>
{
    public void Report(WorkerProgress value) => handler(value);
}
