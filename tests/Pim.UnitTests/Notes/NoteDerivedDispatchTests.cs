using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteDerivedDispatchTests
{
    [Fact]
    public async Task DispatchEnqueuesTheActiveDerivedTaskWithOwnerAndTaskIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-derived-dispatch",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NoteDerivedStore(Options.Create(new NoteMaterialUploadOptions
            {
                StorageRoot = root
            }));
            var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
            var snapshot = NoteDerivedFixture.Snapshot(material);
            var plan = new NoteDerivedPlanner().Build(snapshot);
            var owner = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var consent = new NoteDerivedGenerationConsent(Guid.NewGuid(), Guid.NewGuid(),
                "https://fixture.invalid/v1", true);
            await store.CreateTaskAsync(owner,
                new StartNoteDerivedTaskRequest(taskId, owner, snapshot.SourceVersionId, null,
                    consent), plan, "synthetic-fingerprint", default);

            var jobs = new RecordingJobs();
            var dispatch = new NoteDerivedDispatchJob(store, jobs,
                NullLogger<NoteDerivedDispatchJob>.Instance);

            await dispatch.RunAsync(default);

            var job = Assert.Single(jobs.Items);
            Assert.Equal(typeof(NoteDerivedJob), job.Type);
            Assert.Contains(owner, job.Args.OfType<Guid>());
            Assert.Contains(taskId, job.Args.OfType<Guid>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingJobs : Hangfire.IBackgroundJobClient
    {
        public List<Hangfire.Common.Job> Items { get; } = [];

        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
        {
            Items.Add(job);
            return Items.Count.ToString();
        }

        public bool ChangeState(string jobId, Hangfire.States.IState state,
            string expectedState) => throw new NotSupportedException();
    }
}
