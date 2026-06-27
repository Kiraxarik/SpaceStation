// ChunkSpawnSystem.cs
//
// The fixed-grid spawn that used to live in OnCreate has been removed.
// ChunkStreamingSystem now owns all chunk entity creation, driven by the
// player's position at runtime.
//
// This file is intentionally empty. It can be deleted from the project
// unless something still references ChunkSpawnSystem by name.