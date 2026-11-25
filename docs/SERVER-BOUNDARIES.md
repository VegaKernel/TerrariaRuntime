# Integrating a Terraria 1.4.6.1 server host

TerrariaRuntime owns the patched vanilla assembly and hook ABI. A host such as
Vega owns transport and policy, not Terraria's gameplay or `.wld` format.
The patcher pins the Windows dedicated-server assembly by SHA-256.
The package merges the archive's managed `OpenNAT.dll`. Native GNS defaults off
because the package does not bundle the wrapper and its platform-specific
dependencies. A host that supplies compatible native files can opt in by
setting `Netplay.UseGNS` before startup. The default TCP path does not need them.

### Generated hook overhead

Since 1.1.4 a generated method wrapper reads one subscriber snapshot. If no handlers are registered, it forwards the original arguments directly and returns the native result without constructing hook arguments or an original-method delegate. The subscribed path uses the captured multicast delegate; the existing public `Invoke...` signature is preserved. A handler added or removed concurrently is observed on subsequent calls, not halfway through the captured multicast invocation. The `tests/HookEmitterSmoke` project exercises real generated wrappers (static and instance, ref/out, return, cancellation, explicit original call and unsubscribe) and checks allocations on unobserved calls. CI and the publication workflow run this smoke after generation.

| Boundary | Integration | Constraint |
| --- | --- | --- |
| Dedicated TCP listener | Set `Hooks.Netplay.ServerTcpListenerFactory` before `Netplay.StartServer`. Return an `ISocket` listener that owns accepted sockets. Null keeps `CreateTcpListener` and the vanilla socket fallback. | This replaces only the dedicated TCP listener. LAN discovery, Steam and GameNetworkingSockets are separate paths. Preserve callback order, send-buffer lifetime and disconnect semantics. |
| Inbound application | Leave `Hooks.Netplay.ServerPacketPass` unset for vanilla's complete main-thread pass. An installed delegate owns the whole pass; dispatch admitted connections through `NetMessage.CheckBytes(slot)` on that same world thread. | The read completion in `RemoteClient` only appends bytes; do not run `MessageBuffer.GetData` on socket workers. If introducing budgets and a ready queue, preserve packet order per slot and reschedule partial or deferred work. |
| Outbound transport | Start with the socket adapter, existing `Hooks.NetMessage.SendBytes`, and queue instrumentation. Bound bytes per connection and isolate slow peers. | A skipped vanilla send must still satisfy buffer ownership and completion rules. Measure queue age, allocation rate and p99 tick latency before switching defaults. |
| World isolation | Run one vanilla world per Vega worker process. Give each worker separate save paths and ports; reuse Vega's sandbox supervisor and route transfers explicitly. | Vanilla's `Main`, `Netplay`, `WorldGen` and RNG contain shared mutable state. A thread or load context is not the supported world-isolation boundary here. |

### Connection and player lifecycle

`Hooks.Netplay` exposes five connection notifications (all `Action<int, ISocket>`):

* `ServerConnectionActivated`: the socket has been installed in the native slot.
* `ServerPlayerBeforeWorldData`: the initial server-side state transition from 1
  to 2 has occurred, immediately before the first WorldData response. A stock
  client submits its initial appearance, health, mana, equipment and inventory
  state before this transition. The notification runs on the world thread, once
  on the native transition; later world-data requests do not repeat it. This is
  an SSC capture/preparation boundary, **not** proof that incoming data is
  complete, trustworthy, or authenticated. Do not block the world thread on
  storage I/O; authentication and asynchronous admission remain host policy.
* `ServerPlayerEnteringWorld`: the game's initial connection state reaches 10,
  on the world thread and before vanilla's initial player sync and greeting.
  This is not an authentication decision; registration, SSC policy and account
  authority remain host responsibilities.
* `ServerConnectionClosing`: the native reset is about to begin, while the old
  socket, character and `RemoteClient.Data`/`ClientUUID` remain available. Copy
  any final immutable character data synchronously if in the world thread;
  never capture the character from a worker after reset.
* `ServerConnectionClosed`: the original socket is passed after vanilla reset.

The socket identity must accompany the slot in lifecycle consumers so a delayed
close cannot discard newer connection state. Closing, before-world-data and entering-world events
are notifications, not veto callbacks: observer exceptions are logged and
vanilla continues. Reset can occur outside the running world loop, so hosts
should still check world-thread ownership before reading mutable player data.

## Damage authority

`Hooks.MessageBuffer.GetData` can reject an inbound claim before gameplay side effects.
Vanilla NPC strike handling (packet 28) checks the NPC generation but still uses
client-supplied damage, crit and knockback. A generation check or a caller ID
alone does not prove the hit. PvP hurt (117) and death (118) are separate paths.

Keep authoritative hit evidence in Vega on the world thread: authenticated
attacker, server-observed weapon or projectile, target slot and generation,
reach/collision, cadence and one-time consumption. Reject an unmatched claim or
use the server-derived result, then let Terraria apply the hit and publish its
outcome. Do not simply change damage inside a generic NPC strike hook: vanilla's
packet handler also relays the original report, and its acknowledgement behavior
needs explicit testing if packet 28 is intercepted. Start with one supported
damage path and test invalid ownership, stale target, duplicate hits and client
prediction reconciliation before enabling strict authority. No damage rule is
enabled by the transport hooks.

### Local world serialization buffer (1.1.5)

The server patch matches the native `WorldFile.InternalSaveWorld` serialization/write/re-read IL shape before replacing `MemoryStream.ToArray()`. The local path may reuse the backing array only at offset zero and passes its exact written byte count to native `FileUtilities.Write`. Cloud and non-exposable/nonzero-offset buffers retain their original exact copy. The existing readback, validation, rollback and rolling backups are not changed. `tests/WorldSaveBufferSmoke` exercises the generated helper after stream disposal, the native exact-count file writer and all fallback cases. Real medium/small world save/reload checks complement this smoke.