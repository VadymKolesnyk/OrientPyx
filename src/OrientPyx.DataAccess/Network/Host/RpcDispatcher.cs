using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.BusinessLogic.Services.Store;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// Executes <c>POST /rpc/{iface}/{method}</c>: finds the interface (<see cref="Wire.Interfaces"/>) and the method by
/// name, resolves the target from DI (so the notifying decorator and the routers are in the chain), maps the JSON
/// argument array onto the parameters — skipping <see cref="CancellationToken"/> and <c>IProgress&lt;&gt;</c>,
/// translating identifiers to host paths (<see cref="EventPathMapper"/>), filling missing trailing arguments with the
/// parameter defaults — runs the call stamped with the station's name as the change origin, and serialises the result
/// (host paths back to identifiers). Job methods start a background job and answer <see cref="JobStarted"/>.
/// </summary>
internal sealed class RpcDispatcher
{
    private const string EventFolderPathParameter = "eventFolderPath";

    private readonly IServiceProvider _services;
    private readonly EventPathMapper _mapper;
    private readonly JobRegistry _jobs;
    private readonly ConcurrentDictionary<(Type, string), MethodInfo?> _methods = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ensureCreatedGates = new(StringComparer.OrdinalIgnoreCase);

    public RpcDispatcher(IServiceProvider services, EventPathMapper mapper, JobRegistry jobs)
    {
        _services = services;
        _mapper = mapper;
        _jobs = jobs;
    }

    public async Task<RpcOutcome> InvokeAsync(
        string ifaceName, string methodName, JsonElement[] args, StationContext station, Action<string?> touched,
        CancellationToken cancellationToken)
    {
        if (!Wire.Interfaces.TryGetValue(ifaceName, out var iface))
            return RpcErrors.Outcome(404, RpcErrorKinds.NotFound, $"Unknown interface '{ifaceName}'.", typeof(KeyNotFoundException).FullName);

        var method = _methods.GetOrAdd((iface, methodName), key => key.Item1.GetMethod(key.Item2, BindingFlags.Public | BindingFlags.Instance));
        if (method is null)
            return RpcErrors.Outcome(404, RpcErrorKinds.NotFound, $"Unknown method '{ifaceName}.{methodName}'.", typeof(KeyNotFoundException).FullName);

        if (!typeof(Task).IsAssignableFrom(method.ReturnType))
            return RpcErrors.Outcome(400, RpcErrorKinds.BadRequest, $"'{ifaceName}.{methodName}' is not callable over the network.", typeof(ArgumentException).FullName);

        var resultType = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : typeof(void);

        object?[] mapped;
        string? identifier;
        try
        {
            var eventsPath = await _mapper.EventsPathAsync(cancellationToken);
            (mapped, identifier) = BindArguments(method, iface, args, eventsPath);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return RpcErrors.Outcome(400, RpcErrorKinds.BadRequest, ex.Message, typeof(ArgumentException).FullName);
        }

        touched(identifier);
        var target = _services.GetRequiredService(iface);

        if (iface == typeof(IEventStore) && EventStoreMethods.JobMethods.Contains(methodName))
        {
            // Jobs outlive the request: no RequestAborted token, the station polls /jobs/{id}.
            var stationName = station.Name;
            var jobId = _jobs.Start(
                progress =>
                {
                    DataChangeOrigin.Current = stationName;
                    return RunAsync(target, method, Fill(mapped, method, progress, CancellationToken.None), identifier, resultType);
                },
                resultType,
                ex => RpcErrors.Map(ex).Error);
            return new RpcOutcome(200, JsonSerializer.SerializeToUtf8Bytes(new JobStarted(jobId), RpcJson.Options));
        }

        try
        {
            DataChangeOrigin.Current = station.Name;
            var result = await RunAsync(target, method, Fill(mapped, method, null, cancellationToken), identifier, resultType);
            if (resultType == typeof(void))
                return new RpcOutcome(204, null);
            return new RpcOutcome(200, JsonSerializer.SerializeToUtf8Bytes(result, resultType, RpcJson.Options));
        }
        catch (Exception ex)
        {
            return RpcErrors.Outcome(ex);
        }
        finally
        {
            DataChangeOrigin.Current = null;
        }
    }

    // Invokes the method, awaits its task and returns the (path-mapped) result. EnsureCreatedAsync runs under a
    // semaphore per competition so two stations never migrate the same database at once (П13).
    private async Task<object?> RunAsync(object target, MethodInfo method, object?[] args, string? identifier, Type resultType)
    {
        var gate = method.Name == nameof(IEventStore.EnsureCreatedAsync) && identifier is not null
            ? _ensureCreatedGates.GetOrAdd(identifier, _ => new SemaphoreSlim(1, 1))
            : null;
        if (gate is not null)
            await gate.WaitAsync();
        try
        {
            Task task;
            try
            {
                task = (Task)method.Invoke(target, args)!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            await task.ConfigureAwait(false);
            if (resultType == typeof(void))
                return null;
            var value = task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
            return EventPathMapper.MapResult(value);
        }
        finally
        {
            gate?.Release();
        }
    }

    // Maps the JSON arguments onto the parameters in order (CancellationToken / IProgress<> slots stay null here and
    // are filled per call), translating identifiers to paths. Returns the identifier the call addresses, if any.
    private static (object?[] Args, string? Identifier) BindArguments(MethodInfo method, Type iface, JsonElement[] args, string eventsPath)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        string? identifier = null;
        var next = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (IsInjected(p.ParameterType))
                continue;

            object? value;
            if (next < args.Length)
            {
                var element = args[next++];
                value = element.ValueKind == JsonValueKind.Undefined
                    ? Default(p)
                    : element.Deserialize(p.ParameterType, RpcJson.Options);
            }
            else
            {
                value = Default(p);
            }

            if (p.ParameterType == typeof(string) && p.Name == EventFolderPathParameter)
            {
                if (iface == typeof(IEventFolderScanner))
                {
                    value = eventsPath; // the scanner takes the events folder, not a competition
                }
                else
                {
                    identifier = (string?)value;
                    value = EventPathMapper.ToPath(eventsPath, identifier);
                }
            }
            else if (iface == typeof(IEventFolderScanner) && p.ParameterType == typeof(string) && p.Name == "eventsPath")
            {
                value = eventsPath;
            }
            else if (value is EventSummary summary)
            {
                identifier = EventPathMapper.ToIdentifier(summary.FolderPath);
                value = EventPathMapper.Incoming(summary, eventsPath);
            }

            values[i] = value;
        }
        return (values, identifier);
    }

    private static object? Default(ParameterInfo p)
    {
        if (p.HasDefaultValue)
            return p.DefaultValue ?? (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null
                ? Activator.CreateInstance(p.ParameterType)
                : null);
        if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null)
            throw new ArgumentException($"Missing argument '{p.Name}'.");
        return null;
    }

    private static object?[] Fill(object?[] bound, MethodInfo method, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = (object?[])bound.Clone();
        for (var i = 0; i < parameters.Length; i++)
        {
            var t = parameters[i].ParameterType;
            if (t == typeof(CancellationToken))
                args[i] = cancellationToken;
            else if (IsProgress(t))
                args[i] = progress;
        }
        return args;
    }

    private static bool IsInjected(Type t) => t == typeof(CancellationToken) || IsProgress(t);

    private static bool IsProgress(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProgress<>);
}
