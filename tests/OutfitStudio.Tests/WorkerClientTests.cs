using System.Diagnostics;
using OutfitStudio.Core.Protocol;
using OutfitStudio.Plugin;

namespace OutfitStudio.Tests;

/// <summary>Real process-boundary tests. POSIX fixture executables avoid loading Dalamud or a game process.</summary>
public sealed class WorkerClientTests
{
    [PosixFact]
    public async Task ResponseAndProgressCrossProcessBoundaryWithSpacedPaths()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s\n' '{"stage":"test","completed":1,"total":1,"message":"finished"}'
            printf '%s' '{"protocolVersion":2,"success":true,"convertedModelCount":7}' > "$4"
            """);
        var updates = new List<WorkerProgress>();
        var result = await fixture.Client.RunAsync(new WorkerRequest(), executable, "dotnet", updates.Add, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(7, result.ConvertedModelCount);
        Assert.Equal("finished", Assert.Single(updates).Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task DestinationSizingExplainsWhenAnOldWorkerWasLeftInstalled()
    {
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s' '{"protocolVersion":2,"success":true}' > "$4"
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.RunAsync(
            new WorkerRequest { Mode = ConversionMode.DestinationSizes }, executable, "dotnet", _ => { }, CancellationToken.None));
        Assert.Contains("including the worker folder", error.Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task MissingResponseReportsExitAndStderr()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("printf '%s' 'fixture error' >&2\nexit 9");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.RunAsync(
            new WorkerRequest(), executable, "dotnet", _ => { }, CancellationToken.None));
        Assert.Contains("exit 9", error.Message);
        Assert.Contains("fixture error", error.Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task RejectsDifferentProtocolVersion()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("printf '%s' '{\"protocolVersion\":999,\"success\":true}' > \"$4\"");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.RunAsync(
            new WorkerRequest(), executable, "dotnet", _ => { }, CancellationToken.None));
        Assert.Contains("versions do not match", error.Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task MultipleDestinationsRejectAWorkerThatOnlyUnderstandsLegacyRequests()
    {
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s' '{"protocolVersion":1,"success":true}' > "$4"
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.RunAsync(
            new WorkerRequest { Mode = ConversionMode.DestinationSizes, TargetModPaths = ["MAIN", "EXTRA"] },
            executable, "dotnet", _ => { }, CancellationToken.None));
        Assert.Contains("versions do not match", error.Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task RejectsSuccessfulResponseWhenProcessFailed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("printf '%s' '{\"protocolVersion\":2,\"success\":true}' > \"$4\"\nexit 3");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.RunAsync(
            new WorkerRequest(), executable, "dotnet", _ => { }, CancellationToken.None));
        Assert.Contains("exit 3", error.Message);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task CancellationGivesWorkerOpportunityToCleanUp()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s\n' '{"stage":"ready","completed":0,"total":1,"message":"ready"}'
            read -r command
            test "$command" = cancel
            printf '%s' cleanup-completed > cleaned.txt
            """);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Client.RunAsync(new WorkerRequest(), executable, "dotnet", _ => ready.TrySetResult(), cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("cleanup-completed", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "cleaned.txt")));
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task CancellationTerminatesUnresponsiveWorker()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s\n' '{"stage":"ready","completed":0,"total":1,"message":"ready"}'
            sleep 60
            """);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Client.RunAsync(new WorkerRequest(), executable, "dotnet", _ => ready.TrySetResult(), cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 4, 14);
        fixture.AssertJobsRemoved();
    }

    [PosixFact]
    public async Task LateCancellationPreservesPublishedOutputPath()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var executable = fixture.Script("""
            printf '%s\n' '{"stage":"ready","completed":0,"total":1,"message":"ready"}'
            read -r command
            test "$command" = cancel
            printf '%s' '{"protocolVersion":2,"success":true,"outputDirectory":"/saved/converted-mod"}' > "$4"
            """);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Client.RunAsync(new WorkerRequest { Operation = WorkerOperation.Convert }, executable,
            "dotnet", _ => ready.TrySetResult(), cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var response = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("/saved/converted-mod", response.OutputDirectory);
        Assert.True(cancellation.IsCancellationRequested);
        fixture.AssertJobsRemoved();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Outfit Studio worker tests " + Guid.NewGuid().ToString("N"));
        public WorkerClient Client { get; }

        public Fixture() { Directory.CreateDirectory(Root); Client = new WorkerClient(Root, Root); }

        public string Script(string body)
        {
            var path = Path.Combine(Root, "fixture worker's executable.sh");
            File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void AssertJobsRemoved() => Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Root, "jobs")));
        public void Dispose() => Directory.Delete(Root, true);
    }
}

internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Process fixtures require a POSIX shell.";
    }
}
