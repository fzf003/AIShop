using Microsoft.Agents.AI;

// Quick check — write available types to console
Console.WriteLine("=== Types in Microsoft.Agents.AI ===");
foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
{
    if (asm.FullName?.Contains("Agents") == true)
    {
        Console.WriteLine($"Assembly: {asm.FullName}");
        foreach (var t in asm.GetExportedTypes().Take(30))
            Console.WriteLine($"  {t.Namespace}.{t.Name}");
    }
}
