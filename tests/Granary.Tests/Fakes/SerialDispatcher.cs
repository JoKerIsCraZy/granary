using System.Collections.Concurrent;
using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>
/// A UI thread of its own, as the app's DispatcherQueue is: posted work runs there one item at a time, in order. It sets
/// no SynchronizationContext, so code that only relies on <c>await</c> coming back to the UI thread leaves it, which
/// <see cref="HasThreadAccess"/> shows.
/// </summary>
internal sealed class SerialDispatcher : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;

    public SerialDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    /// <summary>What posted work threw; the thread goes on with the next.</summary>
    public ConcurrentQueue<Exception> Failures { get; } = new();

    public bool HasThreadAccess => Thread.CurrentThread == _thread;

    public void Post(Action action) => _work.Add(action);

    /// <summary>Runs <paramref name="work"/> on the UI thread and answers what it returned.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Starts <paramref name="work"/> on the UI thread and waits for it off that thread: work that ends there would
    /// otherwise go on with the test on the UI thread, where waiting for it again (<see cref="Read"/>) would never end.
    /// </summary>
    public async Task RunAsync(Func<Task> work)
    {
        var running = await InvokeAsync(work).ConfigureAwait(false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = running.ContinueWith(task =>
        {
            if (task.Exception is { } failure)
            {
                done.SetException(failure.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                done.SetCanceled();
            }
            else
            {
                done.SetResult();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await done.Task.ConfigureAwait(false);
    }

    /// <summary>Reads <paramref name="read"/> on the UI thread, waiting for it: state the UI thread owns is read there.</summary>
    public T Read<T>(Func<T> read) => InvokeAsync(read).GetAwaiter().GetResult();

    public void Dispose() => _work.CompleteAdding();

    private void Run()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Failures.Enqueue(ex);
            }
        }
    }
}
