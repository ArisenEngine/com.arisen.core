using System.Collections.Concurrent;

namespace ArisenEngine.Core.Diagnostics;

internal enum NotificationDispatcherState
{
    Accepting,
    StopRequested,
    Drained,
    Disposed,
    Faulted
}

internal enum NotificationPostResult
{
    Accepted,
    Stopped,
    Full
}

internal readonly record struct NotificationDispatcherSnapshot(
    NotificationDispatcherState State,
    long AcceptedCount,
    long ProcessedCount,
    long RejectedCount,
    long DroppedCount,
    bool OwnsDispatchTarget);

/// <summary>
/// Owns one ordered notification thread from admission through terminal drain.
/// </summary>
internal sealed class OrderedNotificationDispatcher<T> : IDisposable
{
    private readonly record struct QueuedNotification(long Sequence, T Value);

    private readonly object m_Gate = new();
    private readonly BlockingCollection<QueuedNotification> m_Queue;
    private readonly Thread m_Worker;
    private readonly ManualResetEventSlim m_WorkerCompleted = new(false);
    private readonly TaskCompletionSource m_DisposalCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> m_Failures = new();
    private Action<T>? m_Dispatch;
    private NotificationDispatcherState m_State = NotificationDispatcherState.Accepting;
    private bool m_DisposalClaimed;
    private long m_NextSequence;
    private long m_AcceptedCount;
    private long m_ProcessedCount;
    private long m_RejectedCount;
    private long m_DroppedCount;

    public OrderedNotificationDispatcher(
        string threadName,
        int capacity,
        Action<T> dispatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(dispatch);

        m_Dispatch = dispatch;
        m_Queue = new BlockingCollection<QueuedNotification>(
            new ConcurrentQueue<QueuedNotification>(),
            capacity);
        m_Worker = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = threadName
        };
        m_Worker.Start();
    }

    internal bool IsDispatchThread => ReferenceEquals(Thread.CurrentThread, m_Worker);

    public NotificationPostResult Post(T value)
    {
        lock (m_Gate)
        {
            if (m_State != NotificationDispatcherState.Accepting)
            {
                m_RejectedCount++;
                return NotificationPostResult.Stopped;
            }

            long sequence = ++m_NextSequence;
            if (!m_Queue.TryAdd(new QueuedNotification(sequence, value)))
            {
                m_DroppedCount++;
                return NotificationPostResult.Full;
            }

            m_AcceptedCount++;
            return NotificationPostResult.Accepted;
        }
    }

    public NotificationDispatcherSnapshot RequestStop()
    {
        lock (m_Gate)
        {
            RequestStopLocked();
            return CreateSnapshotLocked();
        }
    }

    public NotificationDispatcherSnapshot GetSnapshot()
    {
        lock (m_Gate)
        {
            return CreateSnapshotLocked();
        }
    }

    internal void ReportIntakeFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (m_Gate)
        {
            if (m_State is NotificationDispatcherState.Disposed or NotificationDispatcherState.Faulted)
            {
                m_RejectedCount++;
                return;
            }

            m_Failures.Add(new InvalidOperationException(
                "A managed log callback failed before notification admission completed.",
                failure));
        }
    }

    public void Dispose()
    {
        if (IsDispatchThread)
        {
            throw new InvalidOperationException(
                "The notification dispatcher cannot synchronously drain from its own dispatch thread.");
        }

        bool ownsDisposal;
        lock (m_Gate)
        {
            RequestStopLocked();
            ownsDisposal = !m_DisposalClaimed &&
                m_State is NotificationDispatcherState.StopRequested or NotificationDispatcherState.Drained;
            if (ownsDisposal)
            {
                m_DisposalClaimed = true;
            }
        }

        if (!ownsDisposal)
        {
            m_DisposalCompletion.Task.GetAwaiter().GetResult();
            return;
        }

        var cleanupFailures = new List<Exception>();
        try
        {
            try
            {
                m_WorkerCompleted.Wait();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Failed while waiting for the notification worker to report completion.",
                    error));
            }

            try
            {
                m_Worker.Join();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Failed while joining the completed notification worker.",
                    error));
            }

            try
            {
                m_Queue.Dispose();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Failed to dispose the drained notification queue.",
                    error));
            }

            try
            {
                m_WorkerCompleted.Dispose();
            }
            catch (Exception error)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Failed to dispose the notification completion signal.",
                    error));
            }
        }
        finally
        {
            Exception? terminalFailure;
            lock (m_Gate)
            {
                m_Dispatch = null;
                if (m_DroppedCount != 0)
                {
                    m_Failures.Add(new InvalidOperationException(
                        $"The notification queue reached capacity and dropped {m_DroppedCount} notification(s)."));
                }

                m_Failures.AddRange(cleanupFailures);
                terminalFailure = m_Failures.Count == 0
                    ? null
                    : new AggregateException(
                        "Notification dispatch shutdown completed with attributable failures.",
                        m_Failures);
                m_State = terminalFailure == null
                    ? NotificationDispatcherState.Disposed
                    : NotificationDispatcherState.Faulted;
            }

            if (terminalFailure == null)
            {
                m_DisposalCompletion.TrySetResult();
            }
            else
            {
                m_DisposalCompletion.TrySetException(terminalFailure);
            }
        }

        m_DisposalCompletion.Task.GetAwaiter().GetResult();
    }

    private void RequestStopLocked()
    {
        if (m_State != NotificationDispatcherState.Accepting) return;

        m_State = NotificationDispatcherState.StopRequested;
        m_Queue.CompleteAdding();
    }

    private void DispatchLoop()
    {
        try
        {
            foreach (QueuedNotification notification in m_Queue.GetConsumingEnumerable())
            {
                try
                {
                    Action<T>? dispatch = m_Dispatch;
                    if (dispatch == null)
                    {
                        throw new InvalidOperationException(
                            "The notification dispatch target was released before worker drain.");
                    }

                    dispatch(notification.Value);
                }
                catch (Exception error)
                {
                    lock (m_Gate)
                    {
                        m_Failures.Add(new InvalidOperationException(
                            $"Notification #{notification.Sequence} failed during dispatch.",
                            error));
                    }
                }
                finally
                {
                    Interlocked.Increment(ref m_ProcessedCount);
                }
            }
        }
        catch (Exception error)
        {
            lock (m_Gate)
            {
                m_Failures.Add(new InvalidOperationException(
                    "The notification worker terminated before its queue drained.",
                    error));
            }
        }
        finally
        {
            lock (m_Gate)
            {
                if (m_State == NotificationDispatcherState.StopRequested)
                {
                    m_State = NotificationDispatcherState.Drained;
                }
            }

            m_WorkerCompleted.Set();
        }
    }

    private NotificationDispatcherSnapshot CreateSnapshotLocked()
    {
        return new NotificationDispatcherSnapshot(
            m_State,
            m_AcceptedCount,
            Interlocked.Read(ref m_ProcessedCount),
            m_RejectedCount,
            m_DroppedCount,
            m_Dispatch != null);
    }
}
