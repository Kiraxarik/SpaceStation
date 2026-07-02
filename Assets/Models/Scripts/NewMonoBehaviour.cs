using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Present on every bone entity of an animated model instance (root and
/// children alike — see ChunkModelInstanceSystem's skeleton-spawn path).
/// ModelAnimationSystem queries this each tick to know which clip to sample
/// (ModelId → AnimationClipRegistry) and which bone within it (BoneName), and
/// falls back to BindRotation when the clip has no channel for this bone (or
/// no clip exists at all — a purely static skeleton with no animation files).
///
/// ModelId/BoneName as FixedString64Bytes rather than a managed string: this is
/// an IComponentData, which must stay unmanaged/blittable — same reasoning as
/// every other content-id field carried on a component elsewhere in this
/// project's netcode types (block/chunk RPCs use FixedString/blittable ids for
/// the same reason, just applied here to a purely-client rendering component
/// rather than a networked one).
/// </summary>
public struct AnimatedBone : IComponentData
{
    public FixedString64Bytes ModelId;
    public FixedString64Bytes BoneName;
    public quaternion BindRotation;
}