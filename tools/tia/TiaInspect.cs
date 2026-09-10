// Read-only inspection of a TIA Portal V18 project through the Openness API.
//
// This exists as a compiled C# host rather than a PowerShell script for one reason:
// Openness resolves its dependencies on background threads, and a PowerShell
// scriptblock used as an AssemblyResolve handler cannot safely be invoked from an
// arbitrary thread - it recurses until the stack dies. A C# handler can.
//
// Compile with the .NET Framework compiler (C# 5 dialect - no interpolation):
//   csc.exe /target:exe /platform:x64 /out:TiaInspect.exe TiaInspect.cs
//     /r:"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class TiaInspect
{
    private const string ApiDir =
        @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18";

    private static readonly HashSet<string> Resolving = new HashSet<string>();
    private static StreamWriter _log;

    private static int Main(string[] args)
    {
        // Registered before any Openness type is touched. Run() is kept separate and
        // un-inlined so the JIT cannot pull Openness types in while resolving Main.
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        string logPath = args.Length > 1 ? args[1] : "tia-inspect.log";
        using (_log = new StreamWriter(logPath, false))
        {
            _log.AutoFlush = true;
            try
            {
                return Run(args.Length > 0 ? args[0] : null);
            }
            catch (Exception ex)
            {
                Say("!!! ERROR: " + ex.GetType().Name + ": " + ex.Message);
                for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    Say("!!!   inner: " + inner.GetType().Name + ": " + inner.Message);
                Say(ex.StackTrace);
                return 1;
            }
        }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;

        // Hand back an assembly that is already loaded rather than loading a second
        // copy under the same identity - that alone causes a resolve loop.
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (loaded.GetName().Name == name)
                return loaded;

        lock (Resolving)
        {
            if (!Resolving.Add(name))
                return null;
        }
        try
        {
            string dll = Path.Combine(ApiDir, name + ".dll");
            return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
        }
        finally
        {
            lock (Resolving) { Resolving.Remove(name); }
        }
    }

    private static void Say(string text)
    {
        Console.WriteLine(text);
        if (_log != null) _log.WriteLine(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string projectPath)
    {
        var processes = Siemens.Engineering.TiaPortal.GetProcesses();
        Say("=== running TIA Portal processes: " + processes.Count + " ===");
        foreach (var p in processes)
            Say("  pid=" + p.Id + "  project=" + p.ProjectPath);

        if (processes.Count == 0)
        {
            Say("No TIA Portal process is running.");
            return 2;
        }

        Say("=== attaching (TIA may ask you to confirm external access) ===");
        using (var portal = processes[0].Attach())
        {
            Say("attached.");

            Siemens.Engineering.Project project = null;
            bool weOpened = false;

            if (portal.Projects.Count > 0)
            {
                foreach (var p in portal.Projects) { project = p; break; }
                Say("using the already-open project: " + project.Path);
            }
            else if (projectPath != null)
            {
                Say("=== no project open; opening " + projectPath + " ===");
                project = portal.Projects.Open(new FileInfo(projectPath));
                weOpened = true;
            }
            else
            {
                Say("No project open and none supplied.");
                return 3;
            }

            Say("project: " + project.Name + "   path=" + project.Path);
            Say("");
            Say("=== DEVICES ===");
            foreach (var device in project.Devices)
            {
                Say("DEVICE: " + device.Name + "   typeid=" + device.TypeIdentifier);
                foreach (var item in device.DeviceItems)
                    WalkDeviceItem(item, 1);
            }

            foreach (var found in Software)
            {
                var plc = found.Value as Siemens.Engineering.SW.PlcSoftware;
                if (plc == null) continue;

                Say("");
                Say("=== PLC SOFTWARE: " + found.Key + " ===");
                Say("--- program blocks ---");
                WalkBlocks(plc.BlockGroup, "");
                Say("--- PLC tag tables ---");
                WalkTags(plc.TagTableGroup, "");
            }

            // Only tidy up what we opened - leave the user's session as it was.
            if (weOpened && project != null) project.Close();
        }
        Say("=== detached ===");
        return 0;
    }

    private static readonly List<KeyValuePair<string, object>> Software =
        new List<KeyValuePair<string, object>>();

    private static void WalkDeviceItem(Siemens.Engineering.HW.DeviceItem item, int depth)
    {
        string pad = new string(' ', depth * 2);
        Say(pad + "- " + item.Name + "  [" + item.TypeIdentifier + "]");

        var container = item.GetService<Siemens.Engineering.HW.Features.SoftwareContainer>();
        if (container != null && container.Software != null)
        {
            Say(pad + "  >>> software: " + container.Software.GetType().FullName);
            Software.Add(new KeyValuePair<string, object>(item.Name, container.Software));
        }

        // Process-image addresses: these are what MB_SERVER's IB_Read/QB windows land
        // on, so they are the map the bridge's point offsets have to agree with.
        foreach (var addr in item.Addresses)
        {
            Say(pad + "  >>> address: " + addr.IoType +
                " start=" + addr.StartAddress +
                " length=" + addr.Length);
        }

        var netif = item.GetService<Siemens.Engineering.HW.Features.NetworkInterface>();
        if (netif != null)
        {
            foreach (var node in netif.Nodes)
            {
                object addr = null;
                try { addr = node.GetAttribute("Address"); }
                catch { }
                Say(pad + "  >>> netif node: " + node.Name + "  address=" + addr);
            }
        }

        foreach (var child in item.DeviceItems)
            WalkDeviceItem(child, depth + 1);
    }

    private static void WalkBlocks(Siemens.Engineering.SW.Blocks.PlcBlockGroup group, string path)
    {
        foreach (var b in group.Blocks)
            Say("  " + path + "/" + b.Name +
                "   type=" + b.GetType().Name +
                "  number=" + b.Number +
                "  lang=" + b.ProgrammingLanguage);

        foreach (var g in group.Groups)
            WalkBlocks(g, path + "/" + g.Name);
    }

    private static void WalkTags(Siemens.Engineering.SW.Tags.PlcTagTableGroup group, string path)
    {
        foreach (var t in group.TagTables)
        {
            var table = t as Siemens.Engineering.SW.Tags.PlcTagTable;
            if (table == null) continue;
            Say("  TABLE " + path + "/" + table.Name + "  (" + table.Tags.Count + " tags)");
            foreach (var tag in table.Tags)
                Say(string.Format("      {0,-40} {1,-12} {2}",
                    tag.Name, tag.DataTypeName, tag.LogicalAddress));
        }
        foreach (var g in group.Groups)
            WalkTags(g, path + "/" + g.Name);
    }
}
