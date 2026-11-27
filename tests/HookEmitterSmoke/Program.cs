using System.Diagnostics;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using TerrariaRuntime;
using SendHooks = TerrariaRuntime.Hooks.NetMessage;

// Real generated wrappers from the patched server; no mock hook implementation.
global::Terraria.Program.SavePath = Path.GetTempPath();
var projectile = new Projectile();
projectile.UnpackTargetIndex(5, out int npc, out int player);
Require(npc == 5 && player == -1, "Unsubscribed instance/out path");
projectile.UnpackTargetIndex(-7, out npc, out player);
Require(npc == -1 && player == 5, "Unsubscribed instance/out path with negative input");
var explicitInvocation = HookEvents.Terraria.Projectile.InvokeUnpackTargetIndex(projectile, OriginalUnpack, 5, 0, 0);
Require(explicitInvocation.ContinueExecution && explicitInvocation.aiValue == 5,
    "Existing public hook invoker remains callable with its original signature");

global::Terraria.Main.GetMoonlordSmolBulletWarningTimes(out int first, out int second);
Require(first == 40 && second == 10, "Unsubscribed static/out path");

HookEvents.Terraria.Main.GetMoonlordSmolBulletWarningTimes += ReplaceWarningTimes;
try
{
    global::Terraria.Main.GetMoonlordSmolBulletWarningTimes(out first, out second);
    Require(first == 90 && second == 20, "Subscribed static/out cancellation path");
}
finally { HookEvents.Terraria.Main.GetMoonlordSmolBulletWarningTimes -= ReplaceWarningTimes; }

var item = new Item { type = 1, stack = 2, maxStack = 5 };
int amount = 4;
Require(item.TryAddStack(1, ref amount) && item.stack == 5 && amount == 1,
    "Unsubscribed instance/ref/return path");

item.stack = 2;
amount = 77;
HookEvents.Terraria.Item.TryAddStack += ChangeAmount;
try
{
    Require(item.TryAddStack(1, ref amount) && item.stack == 5 && amount == 3,
        "Subscribed continuation uses modified ref argument");
}
finally { HookEvents.Terraria.Item.TryAddStack -= ChangeAmount; }

item.stack = 2;
amount = 1;
HookEvents.Terraria.Item.TryAddStack += CallOriginal;
try
{
    Require(item.TryAddStack(1, ref amount) && item.stack == 5 && amount == 2,
        "Subscribed original delegate uses ref/out and preserves explicit return");
}
finally { HookEvents.Terraria.Item.TryAddStack -= CallOriginal; }

// Post-strike notification is attached to the actual native success return,
// not the before hook or a synthetic packet. Verify native result and life.
int originalMode = global::Terraria.Main.netMode;
int originalPlayer = global::Terraria.Main.myPlayer;
bool originalDedicated = global::Terraria.Main.dedServ;
try
{
    global::Terraria.Main.netMode = 2;
    global::Terraria.Main.myPlayer = 255;
    global::Terraria.Main.dedServ = true;
    var struck = new NPC { whoAmI = 7, generation = 4, active = true, type = 1, life = 100, lifeMax = 100,
        defense = 0, realLife = -1, takenDamageMultiplier = 1, knockBackResist = 0 };
    int observations = 0, observedDamage = 0, observedLife = 0;
    void ThrowingStrikeObserver(NPC target, int damage, int requested, bool critical) => throw new InvalidOperationException("fixture observer");
    void StrikeObserver(NPC target, int damage, int requested, bool critical)
    {
        Require(ReferenceEquals(target, struck) && requested == 10 && !critical, "Native strike arguments preserved");
        observations++;
        observedDamage = damage;
        observedLife = target.life;
    }
    Hooks.NPC.StrikeCompleted += ThrowingStrikeObserver;
    Hooks.NPC.StrikeCompleted += StrikeObserver;
    try
    {
        int damage = struck.StrikeNPC(10, 0, 0, false, true, 1);
        Require(damage == 10 && struck.life == 90 && observedDamage == damage && observedLife == 90 && observations == 1,
            "Native strike completes exactly once with unchanged damage and post-strike life");
        struck.active = false;
        Require(struck.StrikeNPC(10, 0, 0, false, true, 1) == 0 && observations == 1,
            "Native early zero return emits no completion observation");
        struck.active = true;
        void SkipVanillaStrike(NPC target, HookEvents.Terraria.NPC.StrikeNPCEventArgs strikeArgs)
        {
            strikeArgs.ContinueExecution = false;
            strikeArgs.HookReturnValue = 17;
        }
        HookEvents.Terraria.NPC.StrikeNPC += SkipVanillaStrike;
        try
        {
            Require(struck.StrikeNPC(10, 0, 0, false, true, 1) == 17 && struck.life == 90 && observations == 1,
                "Legacy before-hook override does not become a false native completion");
        }
        finally { HookEvents.Terraria.NPC.StrikeNPC -= SkipVanillaStrike; }
    }
    finally
    {
        Hooks.NPC.StrikeCompleted -= ThrowingStrikeObserver;
        Hooks.NPC.StrikeCompleted -= StrikeObserver;
    }
    // Qualification of the new helper itself, separate from native strike work.
    for (int i = 0; i < 10000; i++) Hooks.NPC.NotifyStrikeCompleted(10, struck, 10, false);
    long strikeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    int strikeChecksum = 0;
    for (int i = 0; i < 100000; i++) strikeChecksum += Hooks.NPC.NotifyStrikeCompleted(10, struck, 10, false);
    long strikeAllocated = GC.GetAllocatedBytesForCurrentThread() - strikeAllocatedBefore;
    Require(strikeChecksum == 1000000 && strikeAllocated <= 1024, "Unobserved strike completion allocates no hook state");
    Console.WriteLine($"Unobserved NPC strike completions=100000; allocated={strikeAllocated}");
}
finally
{
    global::Terraria.Main.netMode = originalMode;
    global::Terraria.Main.myPlayer = originalPlayer;
    global::Terraria.Main.dedServ = originalDedicated;
}

var overrideCalls = 0;
HookEvents.HookDelegate<Projectile, HookEvents.Terraria.Projectile.UnpackTargetIndexEventArgs> secondHandler =
    (_, _) => overrideCalls++;
void UnsubscribeDuringInvocation(Projectile _, HookEvents.Terraria.Projectile.UnpackTargetIndexEventArgs args)
{
    HookEvents.Terraria.Projectile.UnpackTargetIndex -= secondHandler;
    args.aiValue = -3;
}
HookEvents.Terraria.Projectile.UnpackTargetIndex += UnsubscribeDuringInvocation;
HookEvents.Terraria.Projectile.UnpackTargetIndex += secondHandler;
try
{
    projectile.UnpackTargetIndex(5, out npc, out player);
    Require(npc == -1 && player == 1 && overrideCalls == 1, "Multicast invocation uses captured subscriber snapshot");
    projectile.UnpackTargetIndex(5, out npc, out player);
    Require(overrideCalls == 1, "Unsubscription is visible on next invocation");
}
finally
{
    HookEvents.Terraria.Projectile.UnpackTargetIndex -= UnsubscribeDuringInvocation;
    HookEvents.Terraria.Projectile.UnpackTargetIndex -= secondHandler;
}

const int iterations = 1_000_000;
long checksum = 0;
for (int i = 0; i < 50_000; i++)
    projectile.UnpackTargetIndex(i & 31, out _, out _);
long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
long started = Stopwatch.GetTimestamp();
for (int i = 0; i < iterations; i++)
{
    projectile.UnpackTargetIndex(i & 31, out npc, out player);
    checksum += npc + player;
}
long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
Require(checksum == 14_500_000, "Microbenchmark original method results");
Console.WriteLine($"Unsubscribed calls={iterations}; allocated={allocated}; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}; checksum={checksum}");
if (!args.Contains("--baseline"))
    Require(allocated <= 1024, "Unobserved calls must not allocate hook state");

// Exercise the real native byte hook ABI, including mixed old/new subscribers.
var socket = new RecordingSocket();
byte[] buffer = [5, 0, 13, 1, 2];
object state = new();
int calls = 0;
SocketSendCallback completion = value =>
{
    Require(ReferenceEquals(value, state), "Native send completion state");
    calls++;
};
bool cancel = false;
int fastCalls = 0, legacyCalls = 0;
void Fast(ref SendHooks.SendBytesContext context)
{
    Require(ReferenceEquals(context.Socket, socket) && ReferenceEquals(context.Data, buffer) &&
        context.Offset == 0 && context.Size == 5 && context.RemoteClient == 7 && ReferenceEquals(context.Callback, completion),
        "Fast hook preserves the original send arguments");
    fastCalls++;
    if (cancel) { context.Cancel(); context.Callback(context.State); }
}
bool legacyCancel = false;
void Legacy(object? _, SendHooks.SendBytesEventArgs eventArgs)
{
    legacyCalls++;
    if (legacyCancel) { eventArgs.Result = HookResult.Cancel; eventArgs.Callback(eventArgs.State); }
}
void Send() => SendHooks.InvokeSendBytes(socket, buffer, 0, 5, completion, state, 7);
SendHooks.SendBytesFast += Fast;
try
{
    Send();
    Require(calls == 1 && socket.Sends == 1 && fastCalls == 1, "Fast path sends once");
    cancel = true;
    Send();
    Require(calls == 2 && socket.Sends == 1 && fastCalls == 2, "Cancelled fast path completes once and does not send");
    cancel = false;
    SendHooks.SendBytes += Legacy;
    try
    {
        Send();
        Require(calls == 3 && socket.Sends == 2 && fastCalls == 3 && legacyCalls == 1, "Mixed legacy/fast path sends once");
        legacyCancel = true;
        Send();
        Require(calls == 4 && socket.Sends == 2 && fastCalls == 3 && legacyCalls == 2, "Legacy cancellation remains authoritative");
    }
    finally { SendHooks.SendBytes -= Legacy; }
    for (int i = 0; i < 50_000; i++) Send();
    long beforeFast = GC.GetAllocatedBytesForCurrentThread();
    long fastStarted = Stopwatch.GetTimestamp();
    for (int i = 0; i < iterations; i++) Send();
    long fastBytes = GC.GetAllocatedBytesForCurrentThread() - beforeFast;
    Console.WriteLine($"Fast send calls={iterations}; allocated={fastBytes}; elapsed_ms={Stopwatch.GetElapsedTime(fastStarted).TotalMilliseconds:F2}");
    Require(fastBytes <= 1024, "Observed fast byte sends must not allocate hook state");
}
finally { SendHooks.SendBytesFast -= Fast; }

long beforeUnobserved = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < iterations; i++) Send();
long unobservedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeUnobserved;
Require(unobservedBytes <= 1024, "Unobserved byte sends must not allocate hook state");
Console.WriteLine($"Unobserved send calls={iterations}; allocated={unobservedBytes}");
var getter = typeof(RemoteClient).GetMethod("TerrariaRuntimeGetServerWriteCallback", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
var firstClient = new RemoteClient();
var secondClient = new RemoteClient();
var firstCallback = (SocketSendCallback)getter.Invoke(firstClient, null)!;
Require(ReferenceEquals(firstCallback, getter.Invoke(firstClient, null)) && ReferenceEquals(firstCallback.Target, firstClient), "Repeated callback uses same native client target/delegate");
var secondCallback = (SocketSendCallback)getter.Invoke(secondClient, null)!;
Require(!ReferenceEquals(firstCallback, secondCallback) && ReferenceEquals(secondCallback.Target, secondClient), "Different client objects never share callbacks");

// Exercise native SendPacket (including its generated subscriber snapshot), not
// merely the byte-hook helper. A borrowed stream is larger than its packet, and
// neither a mutable hook nor an unknown socket may see the backing capacity.
const int fixtureSlot = 7;
global::Terraria.Main.dedServ = true;
global::Terraria.Main.netMode = 2;
NetMessage.buffer[fixtureSlot] = new MessageBuffer { whoAmI = fixtureSlot };
var borrowingSocket = new BorrowedRecordingSocket();
var nativeClient = new RemoteClient { Id = fixtureSlot, State = 10 };
nativeClient.Activate(borrowingSocket);
Netplay.Clients[fixtureSlot] = nativeClient;
using var serialized = new MemoryStream(128);
serialized.Write([5, 0, 13, 1, 2]);
int observedReadOnly = 0;
void ReadOnly(ref SendHooks.ReadOnlySendBytesContext context)
{
    Require(context.Data.Length == 5 && context.Data[0] == 5 && context.RemoteClient == fixtureSlot, "Read-only observer gets exact length");
    observedReadOnly++;
}
SendHooks.SendBytesReadOnly += ReadOnly;
try
{
    SendHooks.SendSerializedPacket(serialized, fixtureSlot);
    Require(borrowingSocket.BorrowedSends == 1 && borrowingSocket.Sends == 0 && borrowingSocket.LastByte == 2, "Borrow-capable socket gets exact span");
    void MutateGenerated(object? _, HookEvents.Terraria.NetMessage.SendPacketEventArgs arguments)
    {
        Require(arguments.data.Length == 5, "Generated subscriber sees the original owned array shape");
        arguments.data[4] = 9;
    }
    HookEvents.Terraria.NetMessage.SendPacket += MutateGenerated;
    try { SendHooks.SendSerializedPacket(serialized, fixtureSlot); }
    finally { HookEvents.Terraria.NetMessage.SendPacket -= MutateGenerated; }
    Require(borrowingSocket.Sends == 1 && borrowingSocket.LastByte == 9 && serialized.GetBuffer()[4] == 2, "Generated mutation isolated from serialization");
    void MutateLegacy(object? _, SendHooks.SendBytesEventArgs arguments)
    {
        Require(arguments.Data.Length == 5, "Legacy subscriber sees exact-sized owned array");
        arguments.Data[4] = 8;
    }
    SendHooks.SendBytes += MutateLegacy;
    try { SendHooks.SendSerializedPacket(serialized, fixtureSlot); }
    finally { SendHooks.SendBytes -= MutateLegacy; }
    Require(borrowingSocket.Sends == 2 && borrowingSocket.LastByte == 8 && serialized.GetBuffer()[4] == 2, "Legacy mutation isolated from serialization");
    SendHooks.SendSerializedPacket(serialized, fixtureSlot);
    Require(borrowingSocket.BorrowedSends == 2 && borrowingSocket.LastByte == 2 && observedReadOnly == 4, "Borrow path resumes after mutable observers detach");
    for (int i = 0; i < 50_000; i++) SendHooks.SendSerializedPacket(serialized, fixtureSlot);
    long beforeBorrow = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < iterations; i++) SendHooks.SendSerializedPacket(serialized, fixtureSlot);
    long borrowedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBorrow;
    Console.WriteLine($"Borrowed native packet calls={iterations}; allocated={borrowedBytes}");
    Require(borrowedBytes <= 1024, "Borrowed native packet path must not materialize per-recipient arrays");
}
finally { SendHooks.SendBytesReadOnly -= ReadOnly; }
var ordinarySocket = new RecordingSocket();
var ordinaryClient = new RemoteClient { Id = fixtureSlot, State = 10 };
ordinaryClient.Activate(ordinarySocket);
Netplay.Clients[fixtureSlot] = ordinaryClient;
SendHooks.SendSerializedPacket(serialized, fixtureSlot);
Require(ordinarySocket.Sends == 1 && ordinarySocket.LastArray!.Length == 5 && !ReferenceEquals(ordinarySocket.LastArray, serialized.GetBuffer()), "Unknown socket retains an independent exact-sized array");
Console.WriteLine("HookEmitter smoke passed.");

static void ReplaceWarningTimes(object? _, HookEvents.Terraria.Main.GetMoonlordSmolBulletWarningTimesEventArgs args)
{
    Require(args.framesBeforeSmolBeam == 0 && args.framesBeforeSmolBeam2 == 0, "Observed out arguments start at default");
    args.framesBeforeSmolBeam = 90;
    args.framesBeforeSmolBeam2 = 20;
    args.ContinueExecution = false;
}

static void OriginalUnpack(float _, out int npc, out int player) => (npc, player) = (-1, -1);

static void ChangeAmount(Item _, HookEvents.Terraria.Item.TryAddStackEventArgs args) => args.amountLeftToAdd = 6;

static void CallOriginal(Item _, HookEvents.Terraria.Item.TryAddStackEventArgs args)
{
    args.amountLeftToAdd = 5;
    args.HookReturnValue = args.OriginalMethod(args.itemType, ref args.amountLeftToAdd);
    args.ContinueExecution = false;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

class RecordingSocket : ISocket
{
    public int Sends { get; private set; }
    public byte[]? LastArray { get; private set; }
    public byte LastByte { get; protected set; }
    public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
    {
        Sends++;
        LastArray = data;
        LastByte = data[offset + size - 1];
        callback(state);
    }
    public void Close() { }
    public bool IsConnected() => true;
    public bool IsDataAvailable() => false;
    public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
    public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
    public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
    public void StopListening() => throw new NotSupportedException();
    public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
}

sealed class BorrowedRecordingSocket : RecordingSocket, IBorrowedPacketSocket
{
    public int BorrowedSends { get; private set; }
    public void SendBorrowed(ReadOnlySpan<byte> packet, SocketSendCallback callback, object? state)
    {
        BorrowedSends++;
        LastByte = packet[^1];
        callback(state);
    }
}
