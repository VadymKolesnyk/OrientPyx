using System.Collections.Concurrent;
using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Store;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The generic remote implementation of a network interface: every call becomes one <see cref="RpcChannel"/> request
/// (<see cref="CancellationToken"/> and <c>IProgress&lt;&gt;</c> arguments are taken out of the array and handled
/// locally), its kind decided by <see cref="EventStoreMethods"/> (reads and writes of <see cref="IEventStore"/>; a
/// name heuristic for the small interfaces), and the host's answer handed back as the method's
/// <see cref="Task"/>/<see cref="Task{T}"/>. An optional interceptor runs chosen methods locally instead (no-ops,
/// the snapshot cache); an optional after-write hook runs when a write completed.
/// </summary>
public class RpcProxy<T> : DispatchProxy where T : class
{
    private static readonly MethodInfo CastGeneric =
        typeof(RpcProxy<T>).GetMethod(nameof(CastAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> CastCache = new();

    private RpcChannel _channel = null!;
    private string _iface = null!;
    private Func<MethodInfo, object?[], object?>? _intercept;
    private Action<MethodInfo, object?[]>? _afterWrite;

    /// <summary>
    /// Builds the proxy. <paramref name="intercept"/> may return a <see cref="Task"/> to answer the call locally (null
    /// ⇒ go to the host); <paramref name="afterWrite"/> runs after a write finished (success or failure).
    /// </summary>
    public static T Create(
        RpcChannel channel,
        Func<MethodInfo, object?[], object?>? intercept = null,
        Action<MethodInfo, object?[]>? afterWrite = null)
    {
        var proxy = Create<T, RpcProxy<T>>();
        var self = (RpcProxy<T>)(object)proxy;
        self._channel = channel;
        self._iface = Wire.NameOf(typeof(T));
        self._intercept = intercept;
        self._afterWrite = afterWrite;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (_intercept?.Invoke(targetMethod, args) is { } local)
            return local;

        if (!typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
            throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} cannot be called over the network.");

        var kind = KindOf(targetMethod.Name);
        var (wireArgs, progress, cancellationToken) = Strip(targetMethod, args);
        var resultType = targetMethod.ReturnType.IsGenericType ? targetMethod.ReturnType.GetGenericArguments()[0] : typeof(void);

        var call = _channel.CallAsync(_iface, targetMethod.Name, wireArgs, kind, resultType, progress, cancellationToken);
        if (kind != RpcCallKind.Read && _afterWrite is { } after)
            call = call.ContinueWith(t =>
            {
                after(targetMethod, args);
                return t.GetAwaiter().GetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        if (resultType == typeof(void))
            return VoidAsync(call);
        return CastCache.GetOrAdd(resultType, t => CastGeneric.MakeGenericMethod(t)).Invoke(null, [call]);
    }

    /// <summary>Reads and writes of <see cref="IEventStore"/> come from its map; on the small interfaces a method that
    /// looks up (<c>Get/Find/Is/Scan/Count</c>) is a read, anything else a write.</summary>
    public static RpcCallKind KindOf(string methodName)
    {
        if (typeof(T) == typeof(IEventStore))
        {
            if (EventStoreMethods.JobMethods.Contains(methodName))
                return RpcCallKind.Job;
            return EventStoreMethods.IsWrite(methodName) ? RpcCallKind.Write : RpcCallKind.Read;
        }
        return methodName.StartsWith("Get", StringComparison.Ordinal)
               || methodName.StartsWith("Find", StringComparison.Ordinal)
               || methodName.StartsWith("Is", StringComparison.Ordinal)
               || methodName.StartsWith("Scan", StringComparison.Ordinal)
               || methodName.StartsWith("Count", StringComparison.Ordinal)
            ? RpcCallKind.Read
            : RpcCallKind.Write;
    }

    /// <summary>The argument array that goes on the wire (without the token and the progress sink) plus the two.</summary>
    public static (object?[] Args, IProgress<ImportProgress>? Progress, CancellationToken Token) Strip(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        var wire = new List<object?>(args.Length);
        IProgress<ImportProgress>? progress = null;
        var token = CancellationToken.None;
        for (var i = 0; i < parameters.Length && i < args.Length; i++)
        {
            var t = parameters[i].ParameterType;
            if (t == typeof(CancellationToken))
                token = args[i] is CancellationToken ct ? ct : CancellationToken.None;
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProgress<>))
                progress = args[i] as IProgress<ImportProgress>;
            else
                wire.Add(args[i]);
        }
        return (wire.ToArray(), progress, token);
    }

    private static async Task VoidAsync(Task<object?> call) => await call.ConfigureAwait(false);

    private static async Task<TResult> CastAsync<TResult>(Task<object?> call)
        => (TResult)(await call.ConfigureAwait(false))!;
}
