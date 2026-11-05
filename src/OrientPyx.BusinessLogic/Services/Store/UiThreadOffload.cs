using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace OrientPyx.BusinessLogic.Services.Store;

/// <summary>
/// Keeps the UI thread off SQLite. The stores' <c>*Async</c> methods complete synchronously (SQLite has no real
/// async I/O), so <c>await store.GetXAsync()</c> on the UI thread runs the whole query — and any wait for a lock
/// held by a background writer, up to the busy timeout — on the UI thread; long enough and Windows shows
/// «Не відповідає». Every reflection-built service proxy routes its calls through <see cref="Invoke"/>: a
/// <see cref="Task"/>-returning method called from a thread that has a <see cref="SynchronizationContext"/> (the
/// UI thread) is started on the thread pool instead, and the caller's <c>await</c> resumes on the UI thread as
/// before. A call from a pool / Kestrel thread (no context) runs inline, so nested proxies hop once. Async-locals
/// (the change origin, the silent-refresh scope) flow with the execution context.
/// </summary>
public static class UiThreadOffload
{
    private static readonly MethodInfo RunGeneric =
        typeof(UiThreadOffload).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, MethodInfo> RunForResult = new();

    /// <summary>Invokes <paramref name="method"/> on <paramref name="target"/>, off the UI thread when it returns a task.</summary>
    public static object? Invoke(object target, MethodInfo method, object?[] args)
    {
        var returnType = method.ReturnType;
        if (SynchronizationContext.Current is null || !typeof(Task).IsAssignableFrom(returnType))
            return Call(target, method, args);

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var run = RunForResult.GetOrAdd(returnType.GetGenericArguments()[0], static t => RunGeneric.MakeGenericMethod(t));
            return run.Invoke(null, [target, method, args]);
        }

        return Task.Run(() => (Task)Call(target, method, args)!);
    }

    private static Task<TResult> RunAsync<TResult>(object target, MethodInfo method, object?[] args)
        => Task.Run(() => (Task<TResult>)Call(target, method, args)!);

    // Reflection wraps a thrown exception in TargetInvocationException; rethrow the original so callers see
    // the same exception type they would from a direct call.
    private static object? Call(object target, MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// A plain forwarding proxy whose only job is <see cref="UiThreadOffload"/> — wraps a service that the UI calls
/// directly (the competition editor, the app settings) so its work never runs on the UI thread.
/// </summary>
public class UiOffloadProxy<T> : DispatchProxy where T : class
{
    private T _inner = null!;

    public static T Create(T inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var proxy = Create<T, UiOffloadProxy<T>>();
        ((UiOffloadProxy<T>)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return UiThreadOffload.Invoke(_inner, targetMethod, args ?? []);
    }
}
