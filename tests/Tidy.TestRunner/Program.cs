using System.IO;
using System.Reflection;
using Tidy.Tests;
using Tidy.App;

// This runner invokes the same xUnit test methods directly. It provides a local
// verification path on machines where VSTest's separate testhost cannot connect.
int passed = 0;
int failed = 0;
var testType = typeof(OrganizerTests);
foreach (MethodInfo method in testType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
{
    bool fact = method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Xunit.FactAttribute");
    bool theory = method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Xunit.TheoryAttribute");
    if (!fact && !theory) continue;

    var argumentSets = theory
        ? method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == "Xunit.InlineDataAttribute")
            .Select(attribute => attribute.ConstructorArguments[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> args
                ? args.Select(argument => argument.Value).ToArray()
                : Array.Empty<object?>()).ToArray()
        : [Array.Empty<object?>()];

    foreach (object?[] arguments in argumentSets)
    {
        string label = arguments.Length == 0 ? method.Name : $"{method.Name}({string.Join(", ", arguments)})";
        var instance = new OrganizerTests();
        try
        {
            object? value = method.Invoke(instance, arguments);
            if (value is Task task) await task.ConfigureAwait(false);
            Console.WriteLine($"PASS {label}");
            passed++;
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : exception;
            Console.WriteLine($"FAIL {label}: {cause}");
            failed++;
        }
        finally { instance.Dispose(); }
    }
}
Console.WriteLine($"{passed} passed, {failed} failed.");

string sessionRoot = Path.Combine(Path.GetTempPath(), "Tidy-ViewModel-" + Guid.NewGuid().ToString("N"));
string firstFolder = Path.Combine(sessionRoot, "first");
string secondFolder = Path.Combine(sessionRoot, "second");
Directory.CreateDirectory(firstFolder); Directory.CreateDirectory(secondFolder);
try
{
    for (int index = 0; index < 500; index++) File.WriteAllText(Path.Combine(firstFolder, $"old-{index}.txt"), "x");
    const string unicodeName = "تقرير_日本語.txt";
    File.WriteAllText(Path.Combine(secondFolder, unicodeName), "x");
    using (var viewModel = new MainViewModel())
    {
        if (viewModel.HasFolder || viewModel.Plans.Count != 0 || viewModel.Categories.Count != 0 || viewModel.CanOrganize || viewModel.CanUndo)
            throw new InvalidOperationException("A fresh view model must start with no folder, preview, category counts, or undo history.");
        viewModel.SelectFolder(firstFolder);
        Task staleScan = viewModel.CurrentPreviewTask;
        viewModel.SelectFolder(secondFolder);
        Task currentScan = viewModel.CurrentPreviewTask;
        await Task.WhenAll(staleScan, currentScan);
        if (viewModel.Plans.Count != 1 || viewModel.Plans[0].FileName != unicodeName || !viewModel.CanOrganize)
            throw new InvalidOperationException("Folder switching did not leave the current Unicode preview in the view model.");
    }
    using (var reopened = new MainViewModel())
    {
        if (reopened.HasFolder || reopened.Plans.Count != 0 || reopened.Categories.Count != 0 || reopened.CanUndo)
            throw new InvalidOperationException("A reopened view model retained state from a prior session.");
    }
    Console.WriteLine("PASS Fresh startup, automatic preview, folder switching, and session reset");
    passed++;
}
catch (Exception exception)
{
    Console.WriteLine($"FAIL ViewModel session behavior: {exception}");
    failed++;
}
finally { try { Directory.Delete(sessionRoot, true); } catch { } }

Console.WriteLine($"Final result: {passed} passed, {failed} failed.");
return failed == 0 && passed > 0 ? 0 : 1;
