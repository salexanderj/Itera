// Each check asserts one Arch 2.1.0 / Arch.System 1.1.0 behaviour that the ecs skill states as a rule.
// "ok" means the behaviour is unchanged. "CHANGED" means the skill's notes for that behaviour need
// re-checking against the new source. Exit code 1 when anything changed.
// Get<T> of a missing component and a query lambda type missing from WithAll are not exercised here:
// both read memory outside the component arrays.
using System.Text.Json;
using Arch.Buffer;
using Arch.Core;
using Arch.System;

const string VerifiedArch = "2.1.0";
const string VerifiedArchSystem = "1.1.0";

var changed = 0;

// The Arch 2.1.0 assembly reports 2.0.0, so the package version comes from the deps file.
Console.WriteLine($"Arch {PackageVersion("Arch")} (notes verified on {VerifiedArch})");
Console.WriteLine($"Arch.System {PackageVersion("Arch.System")} (notes verified on {VerifiedArchSystem})");

Check(
    "a second WithAll replaces the first",
    () =>
    {
        var description = new QueryDescription().WithAll<A>().WithAll<B>();
        var only = description.All.Components;
        return (only.Length == 1 && only[0] == Component<B>.ComponentType, $"All holds {only.Length} type(s)");
    }
);

Check(
    "world events are compiled out",
    () =>
    {
        using var world = World.Create();
        var calls = 0;
        world.SubscribeEntityCreated((in Entity _) => calls++);
        world.Create(new A());
        return (calls == 0, $"handler ran {calls} time(s)");
    }
);

Check(
    "Get through a stale handle reads the entity that reuses the id",
    () =>
    {
        using var world = World.Create();
        var stale = world.Create(new A { V = 1 });
        world.Destroy(stale);
        var reused = world.Create(new A { V = 2 });
        var value = world.Get<A>(stale).V;
        return (stale.Id == reused.Id && !world.IsAlive(stale) && value == 2, $"read V={value}");
    }
);

Check(
    "Destroy through a stale handle destroys the entity that reuses the id",
    () =>
    {
        using var world = World.Create();
        var stale = world.Create(new A());
        world.Destroy(stale);
        var reused = world.Create(new A());
        world.Destroy(stale);
        return (!world.IsAlive(reused), $"reused alive={world.IsAlive(reused)}");
    }
);

Check(
    "Add of a component the entity has corrupts its slot without an exception",
    () =>
    {
        using var world = World.Create();
        var first = world.Create(new A { V = 1 });
        world.Create(new A { V = 2 });
        world.Add(first, new A { V = 99 });
        var slot = world.GetEntityData(first).Slot;
        var count = world.GetArchetype(first).EntityCount;
        return (slot.Index >= count, $"slot index {slot.Index}, archetype holds {count}");
    }
);

Check(
    "default(Entity) and Entity.Null are not alive and not equal",
    () =>
    {
        using var world = World.Create();
        world.Create(new A());
        Entity zero = default;
        var result = !world.IsAlive(zero) && !world.IsAlive(Entity.Null) && zero != Entity.Null;
        return (result, $"default={zero}, Null={Entity.Null}");
    }
);

Check(
    "there is no EntityReference type",
    () =>
    {
        var type = typeof(World).Assembly.GetType("Arch.Core.EntityReference");
        return (type is null, type is null ? "absent" : "present");
    }
);

Check(
    "queries iterate back to front and Destroy moves the last entity into the gap",
    () =>
    {
        using var world = World.Create();
        var entities = new List<Entity>();
        for (var i = 0; i < 5; i++)
        {
            entities.Add(world.Create(new A { V = i }));
        }

        var description = new QueryDescription().WithAll<A>();
        var before = Values(world, description);
        world.Destroy(entities[1]);
        var after = Values(world, description);
        return (before == "4,3,2,1,0" && after == "3,2,4,0", $"before {before}, after {after}");
    }
);

Check(
    "Add inside a query can visit an entity twice",
    () =>
    {
        using var world = World.Create();
        world.Create(new A { V = 100 }, new B());
        for (var i = 0; i < 5; i++)
        {
            world.Create(new A { V = i });
        }

        var visits = new Dictionary<long, int>();
        world.Query(
            new QueryDescription().WithAll<A>(),
            (Entity entity, ref A a) =>
            {
                visits[a.V] = visits.GetValueOrDefault(a.V) + 1;
                if (!world.Has<B>(entity))
                {
                    world.Add(entity, new B());
                }
            }
        );
        var twice = visits.Count(pair => pair.Value > 1);
        return (twice > 0, $"{twice} entities visited twice");
    }
);

Check(
    "Destroy of another entity inside a query can visit an entity twice",
    () =>
    {
        using var world = World.Create();
        var entities = new List<Entity>();
        for (var i = 0; i < 6; i++)
        {
            entities.Add(world.Create(new A { V = i }));
        }

        var visits = new Dictionary<long, int>();
        world.Query(
            new QueryDescription().WithAll<A>(),
            (ref A a) =>
            {
                visits[a.V] = visits.GetValueOrDefault(a.V) + 1;
                if (a.V == 3)
                {
                    world.Destroy(entities[1]);
                }
            }
        );
        var twice = visits.Count(pair => pair.Value > 1);
        return (twice > 0, $"{twice} entities visited twice");
    }
);

Check(
    "Destroy of the current entity inside a query visits each entity once",
    () =>
    {
        using var world = World.Create();
        for (var i = 0; i < 300; i++)
        {
            world.Create(new A { V = i });
        }

        var visits = new Dictionary<long, int>();
        world.Query(
            new QueryDescription().WithAll<A>(),
            (Entity entity, ref A a) =>
            {
                visits[a.V] = visits.GetValueOrDefault(a.V) + 1;
                world.Destroy(entity);
            }
        );
        var once = visits.Count == 300 && visits.Values.All(v => v == 1);
        return (once && world.Size == 0, $"{visits.Count} visited, {world.Size} left");
    }
);

Check(
    "Create inside a query does not visit the new entities",
    () =>
    {
        using var world = World.Create();
        for (var i = 0; i < 300; i++)
        {
            world.Create(new A { V = i });
        }

        var visits = 0;
        world.Query(
            new QueryDescription().WithAll<A>(),
            (ref A _) =>
            {
                visits++;
                world.Create(new A { V = -1 });
            }
        );
        return (visits == 300 && world.Size == 600, $"{visits} visits, {world.Size} entities");
    }
);

Check(
    "CommandBuffer plays back Add before Remove whatever the record order",
    () =>
    {
        using var world = World.Create();
        var entity = world.Create(new A());
        using var buffer = new CommandBuffer();
        buffer.Remove<A>(entity);
        buffer.Add(entity, new A { V = 7 });
        buffer.Playback(world);
        return (!world.Has<A>(entity), $"Has<A>={world.Has<A>(entity)}");
    }
);

Check(
    "CommandBuffer Destroy recorded twice for one entity throws on playback",
    () =>
    {
        using var world = World.Create();
        var entity = world.Create(new A());
        world.Create(new A());
        using var buffer = new CommandBuffer();
        buffer.Destroy(entity);
        buffer.Destroy(entity);
        try
        {
            buffer.Playback(world);
            return (false, "no exception");
        }
        catch (NullReferenceException)
        {
            return (true, "NullReferenceException");
        }
    }
);

Check(
    "parallel queries need the static World.SharedJobScheduler",
    () =>
    {
        using var world = World.Create();
        world.Create(new A());
        try
        {
            world.ParallelQuery(new QueryDescription().WithAll<A>(), (Entity _) => { });
            return (false, "ran without a scheduler");
        }
        catch (Exception ex) when (ex.Message.Contains("SharedJobScheduler", StringComparison.Ordinal))
        {
            return (true, "throws");
        }
    }
);

Check(
    "lambdas that capture a local, a parameter, or this allocate; static lambdas, InlineQuery, and chunk loops do not",
    () =>
    {
        using var world = World.Create();
        for (var i = 0; i < 1000; i++)
        {
            world.Create(new A { V = i });
        }

        var system = new LambdaShapes(world);
        var local = BytesPerCall(system.CapturesLocal);
        var parameter = BytesPerCall(() => system.CapturesParameter(3));
        var instance = BytesPerCall(system.CapturesThis);
        var staticOnly = BytesPerCall(system.StaticLambda);
        var inline = BytesPerCall(system.InlineQuery);
        var chunks = BytesPerCall(system.ChunkLoop);
        var result = local > 0 && parameter > 0 && instance > 0 && staticOnly == 0 && inline == 0 && chunks == 0;
        return (
            result,
            $"bytes per call: local {local}, parameter {parameter}, this {instance}, static {staticOnly}, inline {inline}, chunk loop {chunks}"
        );
    }
);

Check(
    "Group.Dispose disposes its systems",
    () =>
    {
        var system = new CountingSystem();
        var group = new Group<float>("probe", system);
        group.Dispose();
        return (system.Disposed == 1, $"system disposed {system.Disposed} time(s)");
    }
);

Check(
    "Group.Get stops at the first nested group",
    () =>
    {
        var system = new CountingSystem();
        using var group = new Group<float>("outer", new Group<float>("inner"), system);
        var found = group.Get<CountingSystem>();
        return (found is null, found is null ? "not found" : "found");
    }
);

return changed == 0 ? 0 : 1;

void Check(string name, Func<(bool Holds, string Detail)> probe)
{
    (bool Holds, string Detail) result;
    try
    {
        result = probe();
    }
    catch (Exception ex)
    {
        result = (false, $"threw {ex.GetType().Name}: {ex.Message}");
    }

    if (!result.Holds)
    {
        changed++;
    }

    Console.WriteLine($"{(result.Holds ? "ok     " : "CHANGED")} {name} ({result.Detail})");
}

static string PackageVersion(string id)
{
    var deps = Path.Combine(AppContext.BaseDirectory, "ArchProbe.deps.json");
    using var document = JsonDocument.Parse(File.ReadAllText(deps));
    foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
    {
        if (library.Name.StartsWith(id + "/", StringComparison.Ordinal))
        {
            return library.Name[(id.Length + 1)..];
        }
    }

    return "?";
}

static string Values(World world, QueryDescription description)
{
    var values = new List<long>();
    world.Query(in description, (ref A a) => values.Add(a.V));
    return string.Join(',', values);
}

static double BytesPerCall(Action action)
{
    for (var i = 0; i < 200; i++)
    {
        action();
    }

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 1000; i++)
    {
        action();
    }

    return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
}

internal struct A
{
    public long V;
}

internal struct B;

internal struct Sum : IForEach<A>
{
    public long Step;
    public long Total;

    public void Update(ref A a) => Total += a.V * Step;
}

/// <summary>The query shapes a system's Update can use, each run as one Update call.</summary>
internal sealed class LambdaShapes(World world)
{
    private static readonly QueryDescription s_query = new QueryDescription().WithAll<A>();
    private readonly long _step = 2;

    public void CapturesLocal()
    {
        var step = _step;
        world.Query(in s_query, (ref A a) => a.V += step);
    }

    public void CapturesParameter(long step) => world.Query(in s_query, (ref A a) => a.V += step);

    public void CapturesThis() => world.Query(in s_query, (ref A a) => a.V += _step);

    public void StaticLambda() => world.Query(in s_query, static (ref A a) => a.V++);

    public void InlineQuery()
    {
        var sum = new Sum { Step = _step };
        world.InlineQuery<Sum, A>(in s_query, ref sum);
    }

    public void ChunkLoop()
    {
        foreach (ref var chunk in world.Query(in s_query))
        {
            var values = chunk.GetSpan<A>();
            foreach (var index in chunk)
            {
                values[index].V += _step;
            }
        }
    }
}

internal sealed class CountingSystem : BaseSystem<World?, float>
{
    public CountingSystem()
        : base(null) { }

    public int Disposed { get; private set; }

    public override void Dispose()
    {
        Disposed++;
        base.Dispose();
    }
}
