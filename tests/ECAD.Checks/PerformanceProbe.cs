using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ECAD.Core;

namespace ECAD.Checks;

// Opt-in diagnostic, not a timing assertion or a substitute for interactive profiling.
internal static class PerformanceProbe
{
    public static void Run()
    {
        Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
        foreach (var count in new[] { 1000, 5000 })
        {
            var session = new EditorSession();
            session.Load(new DrawingDocument { Elements = Enumerable.Range(0, count).Select(i =>
                new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(i % 100, i / 100), new(i % 100 + .5, i / 100 + .5))).ToArray() });
            var source = session.Document;
            var clone = JsonSerializer.Deserialize<DrawingDocument>(JsonSerializer.Serialize(source))!;
            if (!DocumentContent.Equals(source, clone)) throw new Exception("Cloned content differs");
            Measure($"{count} elements: typed comparison x20", 20, () => DocumentContent.Equals(source, clone));
            Measure($"{count} elements: JSON tree comparison x20", 20, () => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(source), JsonSerializer.SerializeToNode(clone)));
            Measure($"{count} elements: no-op apply x20", 20, () => session.ApplyDocument(source));
            session.Select(source.Elements[0], false);
            Measure($"{count} elements: move/undo x10", 10, () => { session.Move(new(1, 0)); session.Undo(); });
        }
    }

    private static void Measure(string label, int iterations, Action action)
    {
        action(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) action();
        timer.Stop();
        Console.WriteLine($"{label}: {timer.Elapsed.TotalMilliseconds:F2} ms; {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024d / 1024:F2} MiB allocated");
    }
}
