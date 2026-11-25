using System.Reflection;

// Verify the actual helper merged into the generated runtime assembly.
var extract = typeof(Terraria.IO.WorldFile).Assembly.GetType("TerrariaRuntime.WorldSaveBuffer", throwOnError: true)!
    .GetMethod("Extract", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
    .CreateDelegate<ExtractBuffer>();
byte[] expected = [0, 1, 7, 0, 255];

var stream = new MemoryStream(capacity: 1024);
using (var writer = new BinaryWriter(stream))
    writer.Write(expected);
byte[] backing = stream.GetBuffer();
byte[] local = extract(stream, false, out int count);
Require(ReferenceEquals(local, backing) && count == expected.Length && local.Length > count,
    "Disposed local serializer exposes original buffer and exact used length");

// The native local FileUtilities.Write consumes exactly count bytes, never spare capacity.
string path = Path.GetTempFileName();
try
{
    Require(Terraria.Utilities.FileUtilities.Write(path, local, count, false), "Native local file write succeeded");
    Require(File.ReadAllBytes(path).SequenceEqual(expected), "Native file contains the exact serialized prefix");
}
finally { File.Delete(path); }

byte[] cloud = extract(stream, true, out count);
Require(!ReferenceEquals(cloud, backing) && count == expected.Length && cloud.SequenceEqual(expected),
    "Cloud save keeps the exact copied array");

var offsetStream = new MemoryStream(new byte[] { 10, 20, 30, 40 }, 1, 2, writable: true, publiclyVisible: true);
offsetStream.Dispose();
byte[] offset = extract(offsetStream, false, out count);
Require(count == 2 && offset.SequenceEqual(new byte[] { 20, 30 }), "Nonzero buffer offsets fall back to exact copy");

var hiddenStream = new MemoryStream(expected);
hiddenStream.Dispose();
byte[] hidden = extract(hiddenStream, false, out count);
Require(count == expected.Length && hidden.SequenceEqual(expected) && !ReferenceEquals(hidden, expected),
    "Non-public backing buffers fall back to exact copy");

var grownStream = new MemoryStream(capacity: 1);
using (var writer = new BinaryWriter(grownStream))
    writer.Write(expected);
byte[] grown = extract(grownStream, false, out count);
Require(count == expected.Length && ReferenceEquals(grown, grownStream.GetBuffer()) &&
    grown.AsSpan(0, count).SequenceEqual(expected), "Grown streams retain exact used length");
Console.WriteLine("WorldSaveBuffer smoke passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

delegate byte[] ExtractBuffer(MemoryStream stream, bool cloud, out int count);
