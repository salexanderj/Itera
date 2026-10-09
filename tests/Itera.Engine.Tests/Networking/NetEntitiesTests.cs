using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// <see cref="NetEntities"/> and <see cref="PlayerIds"/>: ids map to live entities, a destroyed entity's id no longer
/// resolves, kinds stay apart, and the player ids sit above any id the host hands out.
/// </summary>
public sealed class NetEntitiesTests : IDisposable
{
    private const byte Body = 1;
    private const byte Character = 2;

    private readonly World _world = World.Create();
    private readonly NetEntities _entities = new();

    public void Dispose() => World.Destroy(_world);

    [Fact]
    public void An_id_maps_to_its_live_entity_and_kind()
    {
        var entity = _world.Create(new NetEntity { Id = 1 });
        var id = _entities.NextId();
        _entities.Add(id, entity, Body);

        Assert.True(_entities.TryGet(_world, id, out var found));
        Assert.Equal(entity, found);
        Assert.True(_entities.TryGet(_world, id, out _, out var kind));
        Assert.Equal(Body, kind);
        Assert.True(_entities.TryFindId(entity, out var foundId));
        Assert.Equal(id, foundId);
        Assert.Equal(1u, id);
        Assert.Equal(2u, _entities.NextId());
    }

    [Fact]
    public void A_destroyed_entitys_id_no_longer_resolves_but_stays_known_until_removed()
    {
        var entity = _world.Create(new NetEntity { Id = 1 });
        _entities.Add(1, entity, Body);
        _world.Destroy(entity);

        Assert.False(_entities.TryGet(_world, 1, out var found));
        Assert.Equal(Entity.Null, found);
        Assert.True(_entities.Contains(1));
        Assert.True(_entities.TryFindId(entity, out _));

        _entities.Remove(1);
        Assert.False(_entities.Contains(1));
    }

    [Fact]
    public void A_recycled_entity_does_not_answer_for_the_old_id()
    {
        var old = _world.Create(new NetEntity { Id = 1 });
        _entities.Add(1, old, Body);
        _world.Destroy(old);
        var reused = _world.Create(new NetEntity { Id = 2 });

        Assert.False(_entities.TryGet(_world, 1, out _));
        Assert.False(_entities.TryFindId(reused, out _));
    }

    [Fact]
    public void Kinds_stay_apart()
    {
        var body = _world.Create(new NetEntity { Id = 1 });
        var character = _world.Create(new NetEntity { Id = 2 });
        _entities.Add(1, body, Body);
        _entities.Add(2, character, Character);

        Assert.True(_entities.TryGet(_world, 1, Body, out var found));
        Assert.Equal(body, found);
        Assert.False(_entities.TryGet(_world, 1, Character, out found));
        Assert.Equal(Entity.Null, found);
        Assert.False(_entities.TryGet(_world, 2, Body, out _));

        // A removal naming the wrong kind leaves the entity mapped.
        _entities.Remove(2, Body);
        Assert.True(_entities.Contains(2));
        _entities.Remove(2, Character);
        Assert.False(_entities.Contains(2));
        // An unknown id is a no-op.
        _entities.Remove(99, Body);
    }

    [Fact]
    public void The_collectors_list_one_kind_by_liveness()
    {
        var aliveBody = _world.Create(new NetEntity { Id = 1 });
        var goneBody = _world.Create(new NetEntity { Id = 2 });
        var character = _world.Create(new NetEntity { Id = 3 });
        _entities.Add(1, aliveBody, Body);
        _entities.Add(2, goneBody, Body);
        _entities.Add(3, character, Character);
        _world.Destroy(goneBody);

        var gone = new List<uint>();
        var alive = new List<(uint Id, Entity Entity)>();
        _entities.CollectGone(_world, Body, gone);
        _entities.CollectAlive(_world, Body, alive);

        Assert.Equal([2u], gone);
        Assert.Equal([(1u, aliveBody)], alive);
    }

    [Fact]
    public void Player_ids_go_by_slot_above_the_hosts_ids()
    {
        for (var slot = 0; slot < SessionService.MaxSlots; slot++)
        {
            Assert.Equal(slot, PlayerIds.SlotOf(PlayerIds.Of(slot)));
        }

        Assert.Equal(-1, PlayerIds.SlotOf(PlayerIds.First - 1));
        Assert.Equal(-1, PlayerIds.SlotOf(PlayerIds.Of(SessionService.MaxSlots)));
        Assert.Equal(-1, PlayerIds.SlotOf(1));
    }
}
