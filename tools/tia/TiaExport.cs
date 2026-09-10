// Export every program block of the open TIA project to XML, read-only.
//
// Same compiled-host reasoning as TiaInspect.cs: Openness resolves dependencies on
// background threads, so the AssemblyResolve handler has to be thread-safe.
//
//   csc.exe /target:exe /platform:x64 /out:TiaExport.exe TiaExport.cs
//     /r:"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class TiaExport
{
    private const string ApiDir =
        @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18";

    private static readonly HashSet<string> Resolving = new HashSet<string>();

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            return Run(args.Length > 0 ? args[0] : ".");
        }
        catch (Exception ex)
        {
            Console.WriteLine("!!! ERROR: " + ex.GetType().Name + ": " + ex.Message);
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                Console.WriteLine("!!!   inner: " + inner.GetType().Name + ": " + inner.Message);
            return 1;
        }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (loaded.GetName().Name == name)
                return loaded;

        lock (Resolving) { if (!Resolving.Add(name)) return null; }
        try
        {
            string dll = Path.Combine(ApiDir, name + ".dll");
            return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
        }
        finally { lock (Resolving) { Resolving.Remove(name); } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var processes = Siemens.Engineering.TiaPortal.GetProcesses();
        if (processes.Count == 0) { Console.WriteLine("No TIA Portal running."); return 2; }

        using (var portal = processes[0].Attach())
        {
            Siemens.Engineering.Project project = null;
            foreach (var p in portal.Projects) { project = p; break; }
            if (project == null) { Console.WriteLine("No project open."); return 3; }

            Console.WriteLine("project: " + project.Path);

            foreach (var device in project.Devices)
                foreach (var item in device.DeviceItems)
                    Walk(item, outDir);
        }
        return 0;
    }

    private static void Walk(Siemens.Engineering.HW.DeviceItem item, string outDir)
    {
        var container = item.GetService<Siemens.Engineering.HW.Features.SoftwareContainer>();
        if (container != null)
        {
            var plc = container.Software as Siemens.Engineering.SW.PlcSoftware;
            if (plc != null) ExportBlocks(plc.BlockGroup, outDir);
        }
        foreach (var child in item.DeviceItems) Walk(child, outDir);
    }

    private static void ExportBlocks(Siemens.Engineering.SW.Blocks.PlcBlockGroup group, string outDir)
    {
        foreach (var b in group.Blocks)
        {
            string target = Path.Combine(outDir, b.Name + ".xml");
            try
            {
                if (File.Exists(target)) File.Delete(target);
                // Export fails on a block that has never been compiled; report rather
                // than compiling, which would modify the user's project.
                b.Export(new FileInfo(target), Siemens.Engineering.ExportOptions.WithDefaults);
                Console.WriteLine("exported: " + b.Name + " -> " + target);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED " + b.Name + ": " + ex.Message);
            }
        }
        foreach (var g in group.Groups) ExportBlocks(g, outDir);
    }
}
