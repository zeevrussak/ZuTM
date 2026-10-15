// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.Core.Net;

/// <summary>
/// Cooperative pause signal for long transfers. Unlike a cancellation token,
/// pausing keeps all on-disk state (the staged <c>.part</c> file and its
/// sidecar) so the operation can resume where it stopped.
/// </summary>
public sealed class PauseTokenSource
{
    private volatile bool _paused;
    private TaskCompletionSource _resumed = NewSignal();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PauseToken Token => new(this);

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            if (!value)
            {
                _resumed.TrySetResult();
                _resumed = NewSignal();
            }
        }
    }

    internal async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (_paused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _resumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Read-only view of a <see cref="PauseTokenSource"/>; default means "never pauses".</summary>
public readonly record struct PauseToken(PauseTokenSource? Source)
{
    public bool IsPaused => Source?.IsPaused ?? false;

    internal Task WaitWhilePausedAsync(CancellationToken cancellationToken) =>
        Source?.WaitWhilePausedAsync(cancellationToken) ?? Task.CompletedTask;
}
