using System.Numerics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Replication;

/// <summary>
/// A host sending three clients 300 movers of a kind of every lane (their phase and heading in the last two) on too
/// few bytes for them all, so each send passes most over, as movers leave
/// the clients' reach and are destroyed and added: every message it sends on the step's workers (the movers captured
/// there, and the clients' packets written there) is the message it sends on the main thread alone, byte for byte; and
/// a send on the workers allocates nothing on any thread.
/// </summary>
[RunsLong]
public sealed class ReplicationWorkersTests(ITestOutputHelper output)
{
    private const int Clients = 3;
    private const int Steps = 600;

    [Fact]
    public void A_hosts_sends_on_the_steps_workers_are_its_sends_on_the_main_thread_byte_for_byte()
    {
        var (here, serial) = Sends(stepThreads: 1);
        var (there, parallel) = Sends(stepThreads: 4);

        // Every send but the first few went to the workers twice, its 300 movers' capture and its three clients' packets;
        // none with one thread.
        Assert.Equal(0, serial);
        Assert.True(parallel > Steps - 20, $"{parallel} passes on the workers.");
        Assert.Equal(here.Count, there.Count);
        for (var i = 0; i < here.Count; i++)
        {
            if (!here[i].AsSpan().SequenceEqual(there[i]))
            {
                Assert.Fail(
                    $"Message {i} of {here.Count} differs on the workers ({there[i].Length} bytes, {here[i].Length})."
                );
            }
        }

        // At least a packet a send to each client.
        var snapshots = here.Count(message => message[0] == (byte)MessageKind.Snapshot);
        Assert.True(snapshots >= Clients * (Steps / 2 - 1), $"{snapshots} snapshots.");
    }

    [Fact]
    public void A_hosts_send_on_the_steps_workers_allocates_nothing_on_any_thread()
    {
        using var hosting = new Hosting(stepThreads: 3);
        for (var tick = 0; tick < 120; tick++)
        {
            hosting.Step(tick);
        }

        hosting.HostTransport.CountSendsOnly = true;
        var system = hosting.Host.Services.GetServices<IFixedUpdateSystem>().OfType<ReplicationSendSystem>().Single();
        var workers = hosting.Workers;
        var start = hosting.Host.Tick + 1000;
        for (var i = 0; i < 200; i++)
        {
            system.Update(new FixedStepTime(1f / 60, start + (ulong)i));
        }

        // A window no collection fell in, since one set off by another thread counts bytes never allocated; the workers'
        // count is the same window's.
        var helpers = long.MaxValue;
        var passes = workers.Passes;
        var allocated = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            var counted = workers.AllocatedBytes;
            for (var i = 200; i < 400; i++)
            {
                system.Update(new FixedStepTime(1f / 60, start + (ulong)i));
            }

            helpers = Math.Min(helpers, workers.AllocatedBytes - counted);
        });

        Assert.True(workers.Passes - passes >= 100, $"{workers.Passes - passes} passes on the workers.");
        Assert.Equal((0L, 0L), (allocated, helpers));
    }

    /// <summary>
    /// Every message the host sends from its clients' join on, over <see cref="Steps"/> steps on
    /// <paramref name="stepThreads"/> step threads, and the passes it ran on the workers; a fingerprint of the messages in
    /// the test's output.
    /// </summary>
    private (List<byte[]> Sends, long Passes) Sends(int stepThreads)
    {
        using var hosting = new Hosting(stepThreads);
        hosting.HostTransport.Recorded = [];
        var passes = hosting.Workers.Passes;
        for (var tick = 0; tick < Steps; tick++)
        {
            hosting.Step(tick);
        }

        var sends = hosting.HostTransport.Recorded;
        output.WriteLine($"{stepThreads} step threads: {sends.Count} messages, {WireFingerprint.Of(sends)}.");
        return (sends, hosting.Workers.Passes - passes);
    }

    /// <summary>
    /// A host of 300 movers on <c>Engine:Replication:BytesPerSecond</c> 40,960 and <see cref="Clients"/> clients joined to
    /// it, with no added delay; each <see cref="Step"/> steps the host and then the clients.
    /// </summary>
    private sealed class Hosting : IDisposable
    {
        private readonly MoverPlan _plan = new()
        {
            Count = 300,
            Speed = 4,
            RelevanceMetres = 420,
            FalloffMetres = 100,
            Wide = true,
        };

        private readonly ServiceProvider _hostProvider;
        private readonly List<(ServiceProvider Provider, HeadlessMatch Match)> _clients = [];

        public Hosting(int stepThreads)
        {
            var options = new Dictionary<string, string?>
            {
                ["Engine:Physics:StepThreads"] = stepThreads.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                ["Engine:Replication:BytesPerSecond"] = "40960",
            };
            var network = new FakeNetwork();
            HostTransport = new FakeTransport(network);
            _hostProvider = ReplicationHarness.Build(options, _plan, HostTransport);
            Host = new HeadlessRun(_hostProvider).Host(seed: 3);
            Assert.True(Host.WaitUntilLoaded());
            for (var i = 0; i < Clients; i++)
            {
                var provider = ReplicationHarness.Build(
                    options,
                    new MoverPlan { Wide = true },
                    new FakeTransport(network)
                );
                var client = new HeadlessRun(provider).Join(Host);
                _clients.Add((provider, client));
                Assert.True(client.WaitUntilLoaded());
            }

            Workers = _hostProvider.GetRequiredService<StepWorkers>();
        }

        public FakeTransport HostTransport { get; }

        public HeadlessMatch Host { get; }

        public StepWorkers Workers { get; }

        /// <summary>
        /// One step of the host and then the clients; at some, first a mover goes and another comes, so ids are removed,
        /// freed, and given again.
        /// </summary>
        public void Step(int tick)
        {
            if (tick % 15 == 7)
            {
                var (gone, _) = _plan.Movers[tick % _plan.Movers.Count];
                if (Host.World.IsAlive(gone))
                {
                    Host.World.Destroy(gone);
                }

                var made = Host.World.Create(new TestMover { Position = new Vector3(tick % 97 * 3, 0, 5) });
                _plan.Movers.Add((made, Host.Match.Replication.Add(made, _plan.Mover)));
            }

            Host.Step(1);
            foreach (var (_, client) in _clients)
            {
                client.Step(1);
            }
        }

        public void Dispose()
        {
            foreach (var (provider, client) in _clients)
            {
                client.Dispose();
                provider.Dispose();
            }

            Host.Dispose();
            _hostProvider.Dispose();
        }
    }
}
