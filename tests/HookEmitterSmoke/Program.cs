using System.Diagnostics;
using Terraria;

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
