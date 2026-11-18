using System.Reflection;
using System.Runtime.ExceptionServices;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Store;
using OrientPyx.BusinessLogic.Services.Undo;

namespace OrientPyx.DataAccess.Persistence.Undo;

/// <summary>
/// The decorator right over the local <see cref="EventStore"/> (inside the router, so only in the process that owns
/// <c>event.db</c> — locally and on a host, for the stations' calls too):
/// <list type="bullet">
/// <item>a read runs with no undo step — the step is taken off in the synchronous part of the call and put back as
/// soon as the method has handed its task out, so the async method keeps the empty step and the caller its own. A read
/// that writes (the FSOU discount seeding) never becomes part of a step and a revert never removes it;</item>
/// <item>a write under a step registers the step in the journal first, so a step whose writes changed nothing is
/// «empty», not «never seen».</item>
/// </list>
/// </summary>
public class UndoStoreGuard : DispatchProxy
{
    private IEventStore _inner = null!;

    public static IEventStore Create(IEventStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var proxy = Create<IEventStore, UndoStoreGuard>();
        ((UndoStoreGuard)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (EventStoreMethods.IsRead(targetMethod.Name))
        {
            using (UndoScope.Suppress())
                return Call(targetMethod, args);
        }

        if (UndoScope.Current is { } step && UndoScope.IsActive(step) && args.Length > 0 && args[0] is string path)
            UndoJournal.Shared.Touch(path, step, UndoScope.Owner);
        return Call(targetMethod, args);
    }

    private object? Call(MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
