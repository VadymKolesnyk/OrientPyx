using System.Collections.Concurrent;
using System.Text.Json;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// The host's background jobs (<see cref="BusinessLogic.Services.Store.EventStoreMethods.JobMethods"/>): a job method
/// starts here, runs on a pool thread with a progress sink, and the station polls <c>GET /jobs/{id}</c> until it is
/// finished. Finished jobs are kept ten minutes so a station that lost its connection can still collect the result.
/// </summary>
internal sealed class JobRegistry
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private sealed class Job : IProgress<ImportProgress>
    {
        public required string Id { get; init; }
        public volatile bool Finished;
        public string? Stage;
        public int Current;
        public int Total;
        public JsonElement? Result;
        public RpcError? Error;
        public DateTime? FinishedAt;

        public void Report(ImportProgress value)
        {
            Stage = value.Stage.ToString();
            Current = value.Current;
            Total = value.Total;
        }

        public JobStatus Status => new(Id, Finished, Stage, Current, Total, Result, Error);
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>Starts <paramref name="work"/> in the background; its result is serialised as
    /// <paramref name="resultType"/> once it completes, an exception as an <see cref="RpcError"/>.</summary>
    public string Start(Func<IProgress<ImportProgress>, Task<object?>> work, Type resultType, Func<Exception, RpcError> mapError)
    {
        Prune();
        var job = new Job { Id = Guid.NewGuid().ToString("N") };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(job);
                job.Result = resultType == typeof(void)
                    ? null
                    : JsonSerializer.SerializeToElement(result, resultType, RpcJson.Options);
            }
            catch (Exception ex)
            {
                job.Error = mapError(ex);
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
                job.Finished = true;
            }
        });
        return job.Id;
    }

    public JobStatus? Get(string id) => _jobs.TryGetValue(id, out var job) ? job.Status : null;

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Keep;
        foreach (var (id, job) in _jobs)
            if (job.FinishedAt is { } at && at < cutoff)
                _jobs.TryRemove(id, out _);
    }
}
