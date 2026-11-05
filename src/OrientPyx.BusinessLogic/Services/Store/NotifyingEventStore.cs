using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Network;

namespace OrientPyx.BusinessLogic.Services.Store;

/// <summary>
/// The outermost <see cref="IEventStore"/> in DI: after every successful write it publishes the touched
/// <see cref="DataArea"/>s on the <see cref="IDataChangeBus"/>, stamped with the writer's origin (the calling station's
/// name on a host, else this machine's). On a station it publishes nothing — the host's change stream is the only
/// source of events there, so one write gives exactly one event everywhere.
/// </summary>
public static class NotifyingEventStore
{
    public static IEventStore Create(IEventStore inner, IDataChangeBus bus, IWorkspaceMode mode)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var proxy = DecoratorProxy<IEventStore>.Create(inner, (method, args) =>
        {
            if (mode.Role == WorkspaceRole.Station || !EventStoreMethods.IsWrite(method.Name))
                return null;
            var path = args.Length > 0 && args[0] is string p ? p : string.Empty;
            return () =>
            {
                var origin = DataChangeOrigin.Current ?? mode.SelfOrigin;
                foreach (var area in EventStoreMethods.AreasOf(method.Name))
                    bus.Publish(path, area, origin);
            };
        });
        return proxy;
    }
}

/// <summary>
/// The same decorator over <see cref="IAppStore"/>: the writes to the points rules, ranks, qualification table and rank
/// conditions publish <see cref="DataArea.AppData"/> (for every competition), so result pages recompute.
/// </summary>
public static class NotifyingAppStore
{
    public static IAppStore Create(IAppStore inner, IDataChangeBus bus, IWorkspaceMode mode)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return DecoratorProxy<IAppStore>.Create(inner, (method, _) =>
        {
            if (!EventStoreMethods.AppDataWrites.Contains(method.Name))
                return null;
            return () => bus.Publish(DataChangeBus.AnyEvent, DataArea.AppData, DataChangeOrigin.Current ?? mode.SelfOrigin);
        });
    }
}

/// <summary>
/// A reflection-built decorator: forwards every call of <typeparamref name="T"/> to the inner instance and runs an
/// after-action (chosen per call) once the call — or the <see cref="Task"/> it returned — completed successfully. Keeps
/// the decorators free of hand-written forwarding for ~130 methods.
/// </summary>
public class DecoratorProxy<T> : DispatchProxy where T : class
{
    private T _inner = null!;
    private Func<MethodInfo, object?[], Action?> _after = null!;

    public static T Create(T inner, Func<MethodInfo, object?[], Action?> after)
    {
        var proxy = Create<T, DecoratorProxy<T>>();
        var self = (DecoratorProxy<T>)(object)proxy;
        self._inner = inner;
        self._after = after;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        // Off the UI thread when called from it (see UiThreadOffload); the after-action still follows the task.
        var result = UiThreadOffload.Invoke(_inner, targetMethod, args);

        var after = _after(targetMethod, args);
        if (after is null)
            return result;

        return TaskContinuation.AfterSuccess(result, targetMethod.ReturnType, after);
    }
}

/// <summary>Runs an action after a <see cref="Task"/>/<see cref="Task{T}"/> completes successfully, returning a task of
/// the same type (so a reflection-made proxy can hand it back as the method's result).</summary>
public static class TaskContinuation
{
    private static readonly MethodInfo AfterGeneric =
        typeof(TaskContinuation).GetMethod(nameof(AfterGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly Dictionary<Type, MethodInfo> Cache = new();
    private static readonly object CacheGate = new();

    public static object? AfterSuccess(object? result, Type returnType, Action after)
    {
        if (result is null || returnType == typeof(void) || !typeof(Task).IsAssignableFrom(returnType))
        {
            after();
            return result;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var method = ForResult(returnType.GetGenericArguments()[0]);
            return method.Invoke(null, [result, after]);
        }

        return AfterAsync((Task)result, after);
    }

    private static MethodInfo ForResult(Type resultType)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(resultType, out var method))
            {
                method = AfterGeneric.MakeGenericMethod(resultType);
                Cache[resultType] = method;
            }
            return method;
        }
    }

    private static async Task AfterAsync(Task inner, Action after)
    {
        await inner.ConfigureAwait(false);
        after();
    }

    private static async Task<TResult> AfterGenericAsync<TResult>(Task<TResult> inner, Action after)
    {
        var value = await inner.ConfigureAwait(false);
        after();
        return value;
    }
}
