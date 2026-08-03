using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using ArisenKernel.Contracts;
using Arisen.Native.Diagnostics;

namespace ArisenEngine.Core.Diagnostics;

public static class Logger
{
    private const int NotificationQueueCapacity = 8192;
    private static readonly object s_LifecycleLock = new();
    private static readonly object s_SubscriberLock = new();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(uint type, [MarshalAs(UnmanagedType.LPUTF8Str)] string threadInfo,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string msg, [MarshalAs(UnmanagedType.LPUTF8Str)] string trace);

    private static readonly LogCallback s_ReceiveLog = RecordLog;
    private static OrderedNotificationDispatcher<LogNotification>? s_NotificationDispatcher;
    private static Action<LogMessage>? s_MessageAdded;
    private static Action? s_MessageCleared;
    private static Exception? s_LastShutdownFailure;
    private static bool s_AcceptsSubscribers;
    private static bool s_IsInitialized;
    private static long s_LateNativeCallbackCount;

    internal static void RecordLog(uint type, string threadInfo, string msg, string trace)
    {
        OrderedNotificationDispatcher<LogNotification>? dispatcher =
            Volatile.Read(ref s_NotificationDispatcher);
        if (dispatcher == null)
        {
            Interlocked.Increment(ref s_LateNativeCallbackCount);
            return;
        }

        try
        {
            string threadId = "0";
            string threadName = "Unknown";

            if (!string.IsNullOrEmpty(threadInfo))
            {
                if (long.TryParse(threadInfo, out _))
                {
                    threadId = threadInfo;
                }
                else
                {
                    threadName = threadInfo;
                }
            }

            var message = new LogMessage((LogLevel)type, msg, threadId, threadName, DateTime.Now, trace);
            NotificationPostResult result = dispatcher.Post(LogNotification.Add(message));
            if (result == NotificationPostResult.Stopped)
            {
                Interlocked.Increment(ref s_LateNativeCallbackCount);
            }
        }
        catch (Exception error)
        {
            // Reverse P/Invoke callbacks must not unwind into native logging code.
            dispatcher.ReportIntakeFailure(error);
        }
    }

    private enum LogNotificationKind
    {
        Add,
        Clear
    }

    private readonly record struct LogNotification(LogNotificationKind Kind, LogMessage? Message)
    {
        public static LogNotification Add(LogMessage message) =>
            new(LogNotificationKind.Add, message);

        public static LogNotification Clear() =>
            new(LogNotificationKind.Clear, null);
    }

    public enum LogLevel
    {
        Trace = 0x01,
        Log = 0x02,
        Info = 0x04,
        Warning = 0x08,
        Error = 0x10,
        Fatal = 0x20
    }

    public class LogMessage
    {
        public DateTime Time { get; }
        public LogLevel LogLevel { get; }
        public string Message { get; }
        public string ThreadId { get; }
        public string ThreadName { get; }
        public string StackTrace { get; } = string.Empty;

        public string FullLogString =>
            $"[{Time}] [{LogLevel}] [ThreadId:{ThreadId}, ThreadName:{ThreadName}] \nMessage: {Message} \n" +
            (LogLevel == LogLevel.Log ? "" : StackTrace);

        public LogMessage(LogLevel logLevel, string msg, string threadId, string threadName, DateTime time,
            string stackTrace)
        {
            Time = time;
            LogLevel = logLevel;
            Message = msg;
            ThreadId = threadId;
            ThreadName = threadName;
            StackTrace = stackTrace;
        }
    }

    public static event Action<LogMessage>? MessageAdded
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (s_SubscriberLock)
            {
                if (!s_AcceptsSubscribers)
                {
                    throw new InvalidOperationException(
                        "The diagnostics logger is not accepting event subscribers.");
                }

                s_MessageAdded += value;
            }
        }
        remove
        {
            lock (s_SubscriberLock)
            {
                s_MessageAdded -= value;
            }
        }
    }

    public static event Action? MessageCleared
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (s_SubscriberLock)
            {
                if (!s_AcceptsSubscribers)
                {
                    throw new InvalidOperationException(
                        "The diagnostics logger is not accepting event subscribers.");
                }

                s_MessageCleared += value;
            }
        }
        remove
        {
            lock (s_SubscriberLock)
            {
                s_MessageCleared -= value;
            }
        }
    }

    public static bool IsInitialized => Volatile.Read(ref s_IsInitialized);

    public static void Dispose()
    {
        lock (s_LifecycleLock)
        {
            if (!IsInitialized)
            {
                if (s_LastShutdownFailure != null) throw s_LastShutdownFailure;
                return;
            }

            OrderedNotificationDispatcher<LogNotification>? dispatcher =
                Volatile.Read(ref s_NotificationDispatcher);
            if (dispatcher?.IsDispatchThread == true)
            {
                throw new InvalidOperationException(
                    "The diagnostics logger cannot be shut down from one of its event subscribers.");
            }

            lock (s_SubscriberLock)
            {
                s_AcceptsSubscribers = false;
            }

            var failures = new List<Exception>();
            try
            {
                try
                {
                    LoggerAPI.Logger_Shutdown();
                }
                catch (Exception error)
                {
                    AddFailure(failures, new InvalidOperationException(
                        "Native diagnostics shutdown failed before callback ownership was released.",
                        error));
                }

                if (dispatcher != null)
                {
                    try
                    {
                        dispatcher.RequestStop();
                    }
                    catch (Exception error)
                    {
                        AddFailure(failures, new InvalidOperationException(
                            "Failed to stop managed diagnostics notification admission.",
                            error));
                    }

                    try
                    {
                        dispatcher.Dispose();
                    }
                    catch (Exception error)
                    {
                        AddFailure(failures, error);
                    }
                }

                long lateCallbackCount = Interlocked.Read(ref s_LateNativeCallbackCount);
                if (lateCallbackCount != 0)
                {
                    failures.Add(new InvalidOperationException(
                        $"Native diagnostics issued {lateCallbackCount} callback(s) outside the owned dispatcher lifetime."));
                }
            }
            finally
            {
                Volatile.Write(ref s_NotificationDispatcher, null);
                lock (s_SubscriberLock)
                {
                    s_MessageAdded = null;
                    s_MessageCleared = null;
                }

                Volatile.Write(ref s_IsInitialized, false);
            }

            Exception? shutdownFailure = failures.Count == 0
                ? null
                : new AggregateException(
                    "Diagnostics shutdown completed with one or more attributable failures.",
                    failures);
            s_LastShutdownFailure = shutdownFailure;
            if (shutdownFailure != null) throw shutdownFailure;
        }
    }

    [Conditional("DEBUG")]
    public static void Assert(bool condition, string message = "") => AssertInternal(condition, message);
    public static void Log(object msg) => LogInternal(msg);
    public static void Info(object msg) => InfoInternal(msg);
    public static void Trace(object msg) => TraceInternal(msg);
    public static void Warning(object msg) => WarningInternal(msg);
    public static void Error(object msg) => ErrorInternal(msg);
    public static void Fatal(object msg) => FatalInternal(msg);
    
    [Conditional("DEBUG")]
    private static void AssertInternal(
        bool condition,
        string message = "",
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "")
    {
        if (!condition)
        {
            // Optional: Call native assert if available in the future
            System.Diagnostics.Debug.Assert(condition, message);
        }
    }

    private static void LogInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Log, msg, file, line, function);

    private static void InfoInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Info, msg, file, line, function);

    private static void TraceInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Trace, msg, file, line, function);

    private static void WarningInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Warning, msg, file, line, function);

    private static void ErrorInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Error, msg, file, line, function);

    private static void FatalInternal(object msg,
        [System.Runtime.CompilerServices.CallerFilePath]
        string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber]
        int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName]
        string function = "") => WriteLog(LogLevel.Fatal, msg, file, line, function);

    [SuppressUnmanagedCodeSecurity,
     DllImport("Core.Diagnostic.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Logger_Log")]
    private static extern unsafe void Logger_Log_Internal(Arisen.Native.Diagnostics.LogLevel level, byte* msg,
        LogSourceLocationNative* location, byte* thread_name);

    private struct LogSourceLocationNative
    {
        public unsafe byte* File;
        public unsafe byte* Function;
        public uint Line;
    }

    private static unsafe void WriteLog(LogLevel level, object msg, string file, int line, string function)
    {
        string threadName = Thread.CurrentThread.Name ?? "MainThread";

        // Map engine LogLevel to native LogLevel
        var nativeLevel = level switch
        {
            LogLevel.Trace => Arisen.Native.Diagnostics.LogLevel.Trace,
            LogLevel.Log => Arisen.Native.Diagnostics.LogLevel.Debug,
            LogLevel.Info => Arisen.Native.Diagnostics.LogLevel.Info,
            LogLevel.Warning => Arisen.Native.Diagnostics.LogLevel.Warning,
            LogLevel.Error => Arisen.Native.Diagnostics.LogLevel.Error,
            LogLevel.Fatal => Arisen.Native.Diagnostics.LogLevel.Fatal,
            _ => Arisen.Native.Diagnostics.LogLevel.Info
        };

        string msgStr = msg?.ToString() ?? string.Empty;

        // Calculate needed byte lengths (UTF8 encoding might take up to 3 bytes per char, but we check accurately)
        int msgLen = System.Text.Encoding.UTF8.GetMaxByteCount(msgStr.Length);
        int fileLen = System.Text.Encoding.UTF8.GetMaxByteCount(file.Length);
        int funcLen = System.Text.Encoding.UTF8.GetMaxByteCount(function.Length);
        int threadLen = System.Text.Encoding.UTF8.GetMaxByteCount(threadName.Length);

        // For string lengths under safe limits, use stackalloc. Otherwise use ArrayPool.
        // limit stack alloc to 2048 bytes for message. File/func/thread should never exceed limits.
        const int MaxStackAllocSize = 2048;

        byte[]? msgBuffer = null;
        Span<byte> msgSpan = msgLen <= MaxStackAllocSize
            ? stackalloc byte[msgLen]
            : (msgBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(msgLen));

        Span<byte> fileSpan = stackalloc byte[fileLen];
        Span<byte> funcSpan = stackalloc byte[funcLen];
        Span<byte> threadSpan = stackalloc byte[threadLen];

        try
        {
            // Encode strings to UTF8. Note: GetBytes writes the bytes to the span and returns the actual length.
            // We need to null-terminate the strings for C++
            int actualMsgLen = System.Text.Encoding.UTF8.GetBytes(msgStr, msgSpan);
            msgSpan[actualMsgLen] = 0; // Null terminator

            int actualFileLen = System.Text.Encoding.UTF8.GetBytes(file, fileSpan);
            fileSpan[actualFileLen] = 0;

            int actualFuncLen = System.Text.Encoding.UTF8.GetBytes(function, funcSpan);
            funcSpan[actualFuncLen] = 0;

            int actualThreadLen = System.Text.Encoding.UTF8.GetBytes(threadName, threadSpan);
            threadSpan[actualThreadLen] = 0;

            fixed (byte* pMsg = msgSpan, pFile = fileSpan, pFunc = funcSpan, pThread = threadSpan)
            {
                var loc = new LogSourceLocationNative
                {
                    File = pFile,
                    Function = pFunc,
                    Line = (uint)line
                };

                Logger_Log_Internal(nativeLevel, pMsg, &loc, pThread);
            }
        }
        finally
        {
            if (msgBuffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(msgBuffer);
            }
        }
    }

    public static void Clear()
    {
        OrderedNotificationDispatcher<LogNotification>? dispatcher =
            Volatile.Read(ref s_NotificationDispatcher);
        if (dispatcher != null)
        {
            NotificationPostResult result = dispatcher.Post(LogNotification.Clear());
            if (result == NotificationPostResult.Stopped)
            {
                throw new InvalidOperationException(
                    "The diagnostics notification dispatcher is stopping.");
            }

            return;
        }

        Action? messageCleared;
        lock (s_SubscriberLock)
        {
            if (!s_AcceptsSubscribers) return;
            messageCleared = s_MessageCleared;
        }

        messageCleared?.Invoke();
    }

    public static bool Initialize(bool bindCallback = false)
    {
        lock (s_LifecycleLock)
        {
            if (IsInitialized) return true;

            OrderedNotificationDispatcher<LogNotification>? dispatcher = null;
            bool nativeInitialized = false;
            Interlocked.Exchange(ref s_LateNativeCallbackCount, 0);
            try
            {
                if (bindCallback)
                {
                    dispatcher = new OrderedNotificationDispatcher<LogNotification>(
                        "Arisen Diagnostics Notifications",
                        NotificationQueueCapacity,
                        DispatchNotification);
                    Volatile.Write(ref s_NotificationDispatcher, dispatcher);
                }

                bool ok = LoggerAPI.Logger_Initialize(bindCallback);
                if (!ok)
                {
                    Volatile.Write(ref s_NotificationDispatcher, null);
                    if (dispatcher != null)
                    {
                        dispatcher.RequestStop();
                        dispatcher.Dispose();
                    }

                    return false;
                }

                nativeInitialized = true;
                if (bindCallback)
                {
                    IntPtr ptr = Marshal.GetFunctionPointerForDelegate(s_ReceiveLog);
                    LoggerAPI.Logger_BindCallback(ptr);
                }

                lock (s_SubscriberLock)
                {
                    s_AcceptsSubscribers = true;
                }

                s_LastShutdownFailure = null;
                Volatile.Write(ref s_IsInitialized, true);
                return true;
            }
            catch (Exception initializationError)
            {
                var failures = new List<Exception> { initializationError };
                if (nativeInitialized)
                {
                    try
                    {
                        LoggerAPI.Logger_Shutdown();
                    }
                    catch (Exception shutdownError)
                    {
                        AddFailure(failures, new InvalidOperationException(
                            "Native diagnostics rollback failed.",
                            shutdownError));
                    }
                }

                if (dispatcher != null)
                {
                    try
                    {
                        dispatcher.RequestStop();
                        dispatcher.Dispose();
                    }
                    catch (Exception dispatcherError)
                    {
                        AddFailure(failures, dispatcherError);
                    }
                }

                Volatile.Write(ref s_NotificationDispatcher, null);
                lock (s_SubscriberLock)
                {
                    s_AcceptsSubscribers = false;
                    s_MessageAdded = null;
                    s_MessageCleared = null;
                }

                Volatile.Write(ref s_IsInitialized, false);
                if (failures.Count == 1) throw;
                throw new AggregateException(
                    "Diagnostics initialization failed and rollback reported additional errors.",
                    failures);
            }
        }
    }

    private static void DispatchNotification(LogNotification notification)
    {
        switch (notification.Kind)
        {
            case LogNotificationKind.Add:
            {
                Action<LogMessage>? messageAdded;
                lock (s_SubscriberLock)
                {
                    messageAdded = s_MessageAdded;
                }

                messageAdded?.Invoke(notification.Message!);
                break;
            }
            case LogNotificationKind.Clear:
            {
                Action? messageCleared;
                lock (s_SubscriberLock)
                {
                    messageCleared = s_MessageCleared;
                }

                messageCleared?.Invoke();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(notification));
        }
    }

    private static void AddFailure(List<Exception> failures, Exception error)
    {
        if (error is AggregateException aggregate)
        {
            failures.AddRange(aggregate.Flatten().InnerExceptions);
            return;
        }

        failures.Add(error);
    }
}

/// <summary>
/// Managed bridge that implements the engine-wide ILogger interface.
/// </summary>
public class EngineLogger : ILogger
{
    public void Log(string message) => Logger.Info(message);
    public void LogFormat(string format, params object[] args) => Logger.Info(string.Format(format, args));

    public void Warning(string message) => Logger.Warning(message);
    public void WarningFormat(string format, params object[] args) => Logger.Warning(string.Format(format, args));

    public void Error(string message) => Logger.Error(message);
    public void ErrorFormat(string format, params object[] args) => Logger.Error(string.Format(format, args));

    public void Fatal(string message) => Logger.Fatal(message);
    public void FatalFormat(string format, params object[] args) => Logger.Fatal(string.Format(format, args));

    public void Assert(bool condition, string message = "") => Logger.Assert(condition, message);
    public void AssertFormat(bool condition, string format, params object[] args) => Logger.Assert(condition, string.Format(format, args));
}
