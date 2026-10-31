using System.IO.Pipes;
using System.Text;
using Armux.Core;
using Microsoft.UI.Dispatching;

namespace Armux.App;

/// <summary>
/// Serves the control API (<see cref="Ipc"/>) for the <c>armux</c> CLI. Accepts one connection at a time; each
/// request runs on the UI thread. <see cref="PipeOptions.CurrentUserOnly"/> keeps other users out.
/// </summary>
public sealed class PipeServer(DispatcherQueue ui, Func<Request, Task<Response>> handle) : IDisposable
{
    public static string PipeName { get; } = Ipc.PipeNameFor(Environment.ProcessId);

    readonly CancellationTokenSource stop = new();

    public void Start() => _ = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                // Served on its own so a slow browser command doesn't hold up `armux notify` from a hook.
                _ = ServeAsync(pipe);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                App.Log(nameof(PipeServer), e);
                await Task.Delay(200);
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
                var response = Ipc.DecodeRequest(line ?? "") is { } request
                    ? await OnUiAsync(request)
                    : new Response { Error = "bad request" };
                await writer.WriteLineAsync(Ipc.Encode(response));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { App.Log(nameof(PipeServer), e); }
        }
    }

    Task<Response> OnUiAsync(Request request)
    {
        var done = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Browser commands await the page; the handler starts on the UI thread and its awaits return to it.
        if (!ui.TryEnqueue(async () =>
        {
            try { done.SetResult(await handle(request)); }
            catch (Exception e) { App.Log(nameof(PipeServer), e); done.SetResult(new Response { Error = e.Message }); }
        }))
            done.SetResult(new Response { Error = "armux is closing" });
        return done.Task;
    }

    public void Dispose() => stop.Cancel();
}
