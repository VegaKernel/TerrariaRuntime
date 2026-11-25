# Integrating a Terraria 1.4.6.1 server host

TerrariaRuntime owns the patched vanilla assembly and hook ABI. A host such as
Vega owns transport and policy, not Terraria's gameplay or `.wld` format.
The patcher pins the Windows dedicated-server assembly by SHA-256.
The package merges the archive's managed `OpenNAT.dll`. Native GNS defaults off
because the package does not bundle the wrapper and its platform-specific
dependencies. A host that supplies compatible native files can opt in by
setting `Netplay.UseGNS` before startup. The default TCP path does not need them.

| Boundary | Integration | Constraint |
| --- | --- | --- |
| Dedicated TCP listener | Set `Hooks.Netplay.ServerTcpListenerFactory` before `Netplay.StartServer`. Return an `ISocket` listener that owns accepted sockets. Null keeps `CreateTcpListener` and the vanilla socket fallback. | This replaces only the dedicated TCP listener. LAN discovery, Steam and GameNetworkingSockets are separate paths. Preserve callback order, send-buffer lifetime and disconnect semantics. |
| Inbound application | Leave `Hooks.Netplay.ServerPacketPass` unset for vanilla's complete main-thread pass. An installed delegate owns the whole pass; dispatch admitted connections through `NetMessage.CheckBytes(slot)` on that same world thread. | The read completion in `RemoteClient` only appends bytes; do not run `MessageBuffer.GetData` on socket workers. If introducing budgets and a ready queue, preserve packet order per slot and reschedule partial or deferred work. |
| Outbound transport | Start with the socket adapter, existing `Hooks.NetMessage.SendBytes`, and queue instrumentation. Bound bytes per connection and isolate slow peers. | A skipped vanilla send must still satisfy buffer ownership and completion rules. Measure queue age, allocation rate and p99 tick latency before switching defaults. |
| World isolation | Run one vanilla world per Vega worker process. Give each worker separate save paths and ports; reuse Vega's sandbox supervisor and route transfers explicitly. | Vanilla's `Main`, `Netplay`, `WorldGen` and RNG contain shared mutable state. A thread or load context is not the supported world-isolation boundary here. |

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
