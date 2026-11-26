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

sealed class RecordingSocket : ISocket
{
    public int Sends { get; private set; }
    public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
    {
        Sends++;
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
