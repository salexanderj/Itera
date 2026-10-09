using Arch.Core;
using Itera.Engine.Ecs;

namespace Itera.Engine.Tests.Ecs;

public sealed class CheckedWorldExtensionsTests
{
    [Fact]
    public void Checked_operations_behave_like_arch_on_valid_input()
    {
        using var world = World.Create();
        var entity = world.Create(new Position { X = 1 });

        world.AddChecked(entity, new Velocity { X = 2 });
        world.GetChecked<Position>(entity).X += 10;
        world.SetChecked(entity, new Velocity { X = 3 });

        Assert.Equal(11, world.Get<Position>(entity).X);
        Assert.Equal(3, world.Get<Velocity>(entity).X);

        world.RemoveChecked<Velocity>(entity);
        Assert.False(world.Has<Velocity>(entity));

        world.DestroyChecked(entity);
        Assert.False(world.IsAlive(entity));
    }

#if DEBUG
    [Fact]
    public void Add_of_a_component_the_entity_has_throws()
    {
        using var world = World.Create();
        var entity = world.Create(new Position());

        Assert.Throws<InvalidOperationException>(() => world.AddChecked(entity, new Position()));
    }

    [Fact]
    public void Remove_get_and_set_of_a_missing_component_throw()
    {
        using var world = World.Create();
        var entity = world.Create(new Position());

        Assert.Throws<InvalidOperationException>(() => world.RemoveChecked<Velocity>(entity));
        Assert.Throws<InvalidOperationException>(() => world.GetChecked<Velocity>(entity));
        Assert.Throws<InvalidOperationException>(() => world.SetChecked(entity, new Velocity()));
    }

    [Fact]
    public void Destroy_through_a_stale_handle_throws_and_spares_the_entity_that_reuses_the_id()
    {
        using var world = World.Create();
        var stale = world.Create(new Position());
        world.Destroy(stale);
        var reused = world.Create(new Position());
        Assert.Equal(stale.Id, reused.Id);

        Assert.Throws<InvalidOperationException>(() => world.DestroyChecked(stale));
        Assert.True(world.IsAlive(reused));
    }

    [Fact]
    public void Operations_on_a_default_entity_throw()
    {
        using var world = World.Create();
        world.Create(new Position());

        Assert.Throws<InvalidOperationException>(() => world.DestroyChecked(default));
        Assert.Throws<InvalidOperationException>(() => world.GetChecked<Position>(Entity.Null));
    }

    [Fact]
    public void Query_types_must_be_required_by_the_description()
    {
        var all = new QueryDescription().WithAll<Position, Velocity>();
        var any = new QueryDescription().WithAny<Position, Velocity>();
        var exclusive = new QueryDescription().WithExclusive<Position>();
        var replaced = new QueryDescription().WithAll<Position>().WithAll<Velocity>();

        EcsChecks.RequireAll<Position, Velocity>(in all);
        EcsChecks.RequireAll<Position>(in exclusive);
        Assert.Throws<InvalidOperationException>(() => EcsChecks.RequireAll<Position>(in any));
        // A second WithAll replaces the first, so Position is no longer required.
        Assert.Throws<InvalidOperationException>(() => EcsChecks.RequireAll<Position>(in replaced));
    }
#endif

    private struct Position
    {
        public float X;
    }

    private struct Velocity
    {
        public float X;
    }
}
