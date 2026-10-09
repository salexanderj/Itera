using Arch.System;
using Itera.Modding;

namespace Itera.Engine.Ecs;

/// <summary>
/// A system that runs once per fixed simulation step. It receives the step length and the tick number, which counts
/// this game's steps from its match's start (a client that joins later counts from its own start, not the host's).
/// </summary>
public interface IFixedUpdateSystem : ISystem<FixedStepTime> { }

/// <summary>
/// A system that runs once per rendered frame. It receives the frame delta, the total time, the frame
/// index, and <see cref="FrameTime.Alpha"/> for blending between the last two fixed steps.
/// </summary>
public interface IFrameUpdateSystem : ISystem<FrameTime> { }
