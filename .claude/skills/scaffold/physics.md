# Adding to Itera: physics materials, layers, triggers, and impacts

The `scaffold` skill's recipes for physics materials, layers, triggers, and impacts. Each lists every file the addition touches, in order, and the test that fails when one is missed; finish with the skill's "Then".

## A physics material

1. Register it in the game's `IRunStartup` (injected `PhysicsMaterials`; before the first match, which seals them):
   `var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0))` (friction 0 to 4; bounciness 0 to 1; at most
   64, `default` the engine's, friction 1, no bounce). A voxel material takes one with `materials.MapVoxel(voxel, ice)`
   (the voxels at most 8 physics materials with the default; each voxel cell a static per material).
2. Give it: a body at `PhysicsWorld.AddBody(description, new PhysicsBodySettings { Material = ice })` or
   `SetMaterial`; a mesh collider at `MeshColliders.Add(..., settings)` or `SetSettings`; a ball from nothing takes its
   voxel material's. A system finds it by name once (`materials.TryFind`).
3. Check it: `physics materials`; a test dropping or sliding a body on it (the engine's `PhysicsMaterialsTests` and
   `VoxelPhysicsMaterialsTests`); characters grip by the ground's friction (`CharacterContact.GroundMaterial`).

## A collision layer or group

1. Register it in the game's `IRunStartup` (injected `PhysicsLayers`): `var gate = layers.Register("gate")` (at most
   32), set the run's table (`layers.SetTouches(balls, gate, false)`), and, where characters or voxels need their own,
   `layers.CharacterLayer` and `VoxelLayer`.
2. Give it as a material (above): `PhysicsBodySettings.Layer` and `Group` (bodies and statics of one group other than 0
   never touch each other), or `PhysicsWorld.SetLayer`. In a match, `PhysicsWorld.SetTouches` changes that match's
   table and wakes the sleepers.
3. Remember the mover reads every mesh collider whatever its layer, and no loose body whose layer does not touch the
   characters'. Check it: `physics layers`; a test that a body passes or stops (the engine's `PhysicsLayersTests`).

## A trigger zone

1. Add it from a system's `Initialize` (or later): `var zone = physics.AddTrigger(TriggerShape.Box(size),
   origin.ToLocal(middle), Quaternion.Identity, layer)` (a box, `Sphere`, or `Capsule`; at most 256; `MoveTrigger`,
   `RemoveTrigger`).
2. Read what came and went in a fixed system: bodies in `physics.TriggerEvents` (the last physics step's), characters
   in the match's `CharacterTriggers.Events` (give each character a `TriggerSensor` and register the engine's
   `TriggerCharacterSystem` after the stand-ins; read it in systems after that). Compare `change.Trigger == zone`.
3. Check it: a scenario test walking a character or rolling a body into it (the mesh sample's `PhysicsDemoTests`).

## Impact events

1. Mark the bodies or statics: `PhysicsBodySettings { Marks = PhysicsMarks.ContactEvents }`, or
   `PhysicsWorld.SetMarks` (a ball from `LooseBodies.TryCreateBall` after it is made).
2. Read `physics.ImpactEvents` in a fixed system (the last step's): `Began` with the point, normal, speed, and both
   materials (a sound's gain by the speed, as the mesh sample's `PhysicsDemoSystem` does), `Ended` as a pair parts.
3. Check it: a drop of known height reports its speed (the engine's `ImpactEventsTests`).
