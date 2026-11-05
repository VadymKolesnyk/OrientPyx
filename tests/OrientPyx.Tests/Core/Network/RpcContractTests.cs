using System.Collections;
using System.Reflection;
using System.Text.Json;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Store;
using Xunit;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The contract every network interface must keep so a station can call it through the JSON proxy: no overloads
/// (methods are addressed by name), every parameter and result round-trips through <see cref="RpcJson.Options"/>
/// without loss, <c>IProgress&lt;&gt;</c> only on the listed job methods, synchronous methods only on the listed
/// local ones, no public fields in the model types (<c>IncludeFields</c> would start serialising them), every
/// <see cref="IEventStore"/> method classified as a read or a write, and every method addressing a competition
/// through a parameter named <c>eventFolderPath</c> (or listed as competition-free) so the host can translate it.
/// Fails at build time of the suite when someone adds a method the network cannot carry.
/// </summary>
public sealed class RpcContractTests
{
    private static readonly Type[] NetworkInterfaces =
    [
        typeof(IEventStore), typeof(IEventFolderScanner), typeof(IEventCatalogService),
        typeof(IEventFiles), typeof(IEventArchiveService), typeof(IResultsAppData),
    ];

    // Executed on the station without a call to the host.
    private static readonly HashSet<string> LocalMethods =
    [
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.IsValidIdentifier)}",
        $"{nameof(IEventFiles)}.{nameof(IEventFiles.MonitorOutputDirectory)}",
        $"{nameof(IEventFiles)}.{nameof(IEventFiles.LocalReadoutDirectory)}",
    ];

    // Methods that do not address one competition by its folder path.
    private static readonly HashSet<string> CompetitionFreeMethods =
    [
        $"{nameof(IEventFolderScanner)}.{nameof(IEventFolderScanner.ScanAsync)}",
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.GetEventsAsync)}",
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.FindByIdentifierAsync)}",
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.SetHiddenAsync)}",          // EventSummary argument
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.IsIdentifierAvailableAsync)}",
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.RenameIdentifierAsync)}",   // EventSummary argument
        $"{nameof(IEventCatalogService)}.{nameof(IEventCatalogService.CreateEventAsync)}",
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.ExportAsync)}",
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.PreviewImportAsync)}",
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.ImportAsync)}",
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.IsIdentifierAvailableAsync)}",
        $"{nameof(IEventArchiveService)}.{nameof(IEventArchiveService.IsValidIdentifier)}",
        $"{nameof(IResultsAppData)}.{nameof(IResultsAppData.GetPointsRulesAsync)}",
        $"{nameof(IResultsAppData)}.{nameof(IResultsAppData.GetRanksAsync)}",
        $"{nameof(IResultsAppData)}.{nameof(IResultsAppData.GetRankQualificationAsync)}",
        $"{nameof(IResultsAppData)}.{nameof(IResultsAppData.GetRankConditionsAsync)}",
    ];

    [Fact]
    public void EveryEventStoreMethodIsClassified()
    {
        Assert.Empty(EventStoreMethods.Unclassified());
        var all = typeof(IEventStore).GetMethods().Select(m => m.Name).ToHashSet();
        foreach (var write in EventStoreMethods.Writes.Keys)
            Assert.Contains(write, all);
        foreach (var read in EventStoreMethods.Reads)
            Assert.Contains(read, all);
        foreach (var job in EventStoreMethods.JobMethods)
            Assert.True(EventStoreMethods.IsWrite(job), $"{job} must be a write");
    }

    [Fact]
    public void NoOverloadsOnNetworkInterfaces()
    {
        foreach (var iface in NetworkInterfaces)
        {
            var dupes = iface.GetMethods().GroupBy(m => m.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, $"{iface.Name} has overloads: {string.Join(", ", dupes)}");
        }
    }

    [Fact]
    public void OnlyListedMethodsAreSynchronousOrTakeProgress()
    {
        foreach (var iface in NetworkInterfaces)
        foreach (var method in iface.GetMethods())
        {
            var key = $"{iface.Name}.{method.Name}";
            if (!typeof(Task).IsAssignableFrom(method.ReturnType))
                Assert.True(LocalMethods.Contains(key), $"{key} is synchronous and not listed as local");

            foreach (var p in method.GetParameters())
            {
                var t = p.ParameterType;
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProgress<>))
                    Assert.True(iface == typeof(IEventStore) && EventStoreMethods.JobMethods.Contains(method.Name),
                        $"{key} takes IProgress<> but is not a job method");
                Assert.False(typeof(Delegate).IsAssignableFrom(t), $"{key} takes a delegate");
                Assert.False(typeof(Stream).IsAssignableFrom(t), $"{key} takes a stream");
            }
        }
    }

    [Fact]
    public void EveryMethodAddressesACompetitionOrIsListedAsFree()
    {
        foreach (var iface in NetworkInterfaces)
        foreach (var method in iface.GetMethods())
        {
            var key = $"{iface.Name}.{method.Name}";
            var hasPath = method.GetParameters().Any(p => p.Name == "eventFolderPath" && p.ParameterType == typeof(string));
            Assert.True(hasPath || CompetitionFreeMethods.Contains(key), $"{key} has no eventFolderPath parameter and is not listed");
        }
    }

    [Fact]
    public void EveryParameterAndResultRoundTripsThroughJson()
    {
        var failures = new List<string>();
        foreach (var iface in NetworkInterfaces)
        foreach (var method in iface.GetMethods())
        {
            var key = $"{iface.Name}.{method.Name}";
            if (LocalMethods.Contains(key))
                continue;

            var types = method.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t != typeof(CancellationToken) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProgress<>)))
                .ToList();
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                types.Add(method.ReturnType.GetGenericArguments()[0]);

            foreach (var type in types)
            {
                try
                {
                    var sample = Sample.Of(type);
                    var json = JsonSerializer.Serialize(sample, type, RpcJson.Options);
                    var back = JsonSerializer.Deserialize(json, type, RpcJson.Options);
                    var json2 = JsonSerializer.Serialize(back, type, RpcJson.Options);
                    if (json != json2)
                        failures.Add($"{key}: {type.Name} lost data: {json} → {json2}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{key}: {type.Name}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void NoPublicFieldsInTypesOnTheWire()
    {
        // IncludeFields is on for the ValueTuple batch methods; a public field anywhere else would silently join the
        // JSON. Walk every type reachable from the network interfaces.
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        foreach (var iface in NetworkInterfaces)
        foreach (var method in iface.GetMethods())
        {
            foreach (var p in method.GetParameters())
                queue.Enqueue(p.ParameterType);
            queue.Enqueue(method.ReturnType);
        }

        var offenders = new List<string>();
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                    queue.Enqueue(arg);
                if (Sample.IsValueTuple(type))
                    continue;
            }
            if (type.IsArray)
            {
                queue.Enqueue(type.GetElementType()!);
                continue;
            }
            if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type.Namespace is null)
                continue;
            if (!type.Namespace.StartsWith("OrientPyx", StringComparison.Ordinal))
                continue;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                offenders.Add($"{type.FullName}.{field.Name}");
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                queue.Enqueue(prop.PropertyType);
        }
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void DisciplineIdAndTuplesSerialise()
    {
        var day = new BusinessLogic.Entities.EventDay { DefaultDiscipline = new DisciplineId("plugin.format") };
        var json = JsonSerializer.Serialize(day, RpcJson.Options);
        Assert.Contains("\"plugin.format\"", json);
        Assert.Equal(new DisciplineId("plugin.format"),
            JsonSerializer.Deserialize<BusinessLogic.Entities.EventDay>(json, RpcJson.Options)!.DefaultDiscipline);

        var tuples = new List<(Guid ParticipantId, string Number)> { (Guid.NewGuid(), "12") };
        var back = JsonSerializer.Deserialize<IReadOnlyList<(Guid ParticipantId, string Number)>>(
            JsonSerializer.Serialize(tuples, RpcJson.Options), RpcJson.Options)!;
        Assert.Equal(tuples[0].ParticipantId, back[0].ParticipantId);
        Assert.Equal("12", back[0].Number);
    }

    /// <summary>Builds a filled-in sample of any type found on the wire, so the round trip has something to lose.</summary>
    private static class Sample
    {
        public static object? Of(Type type, int depth = 0)
        {
            if (depth > 6)
                return null;
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null)
                return Of(underlying, depth);

            if (type == typeof(string)) return "x";
            if (type == typeof(int)) return 7;
            if (type == typeof(long)) return 7L;
            if (type == typeof(bool)) return true;
            if (type == typeof(double)) return 1.5;
            if (type == typeof(decimal)) return 2.5m;
            if (type == typeof(Guid)) return Guid.Parse("11111111-2222-3333-4444-555555555555");
            if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));
            if (type == typeof(DateTime)) return new DateTime(2026, 5, 6, 7, 8, 9);
            if (type == typeof(TimeSpan)) return TimeSpan.FromMinutes(61.5);
            if (type == typeof(DisciplineId)) return new DisciplineId("Mixed");
            if (type == typeof(byte[])) return new byte[] { 1, 2, 3 };
            if (type.IsEnum) return Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1);

            if (IsValueTuple(type))
            {
                var args = type.GetGenericArguments().Select(a => Of(a, depth + 1)).ToArray();
                return Activator.CreateInstance(type, args);
            }

            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                var targs = type.GetGenericArguments();
                if (def == typeof(IReadOnlyDictionary<,>) || def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
                {
                    var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(targs))!;
                    dict[Of(targs[0], depth + 1)!] = Of(targs[1], depth + 1);
                    return dict;
                }
                if (def == typeof(IReadOnlySet<>) || def == typeof(HashSet<>) || def == typeof(ISet<>))
                {
                    var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(targs))!;
                    set.GetType().GetMethod("Add")!.Invoke(set, [Of(targs[0], depth + 1)]);
                    return set;
                }
                if (def == typeof(IReadOnlyList<>) || def == typeof(List<>) || def == typeof(IReadOnlyCollection<>)
                    || def == typeof(IEnumerable<>) || def == typeof(IList<>) || def == typeof(ICollection<>))
                {
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targs))!;
                    list.Add(Of(targs[0], depth + 1));
                    return list;
                }
            }

            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType()!, 1);
                array.SetValue(Of(type.GetElementType()!, depth + 1), 0);
                return array;
            }

            if (type.IsInterface || type.IsAbstract)
                throw new InvalidOperationException($"No sample for abstract type {type}");

            // A record / class: the richest constructor, then every settable property.
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
                       ?? throw new InvalidOperationException($"No public constructor on {type}");
            var instance = ctor.Invoke(ctor.GetParameters().Select(p => Of(p.ParameterType, depth + 1)).ToArray());
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    prop.SetValue(instance, Of(prop.PropertyType, depth + 1));
                }
                catch (TargetInvocationException)
                {
                    // A setter that validates (e.g. a normalising property) keeps the constructor's value.
                }
            }
            return instance;
        }

        public static bool IsValueTuple(Type type)
            => type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal);
    }
}
