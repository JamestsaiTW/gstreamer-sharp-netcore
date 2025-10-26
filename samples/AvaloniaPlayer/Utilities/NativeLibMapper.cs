using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Runtime.CompilerServices;


namespace Gst;

public static class NativeLibMapper
{
    private static XElement mappingDocument = null;

    [ModuleInitializer]
    public static void ModuleInit()
    {
        InitNativeLibMapping();
    }

    public static void InitNativeLibMapping()
    {
        // Register resolver on assemblies that issue P/Invoke calls:
        // - gstreamer-sharp (Gst.Application)
        // - glib-sharp (GLib.Object) for glib/gobject/gio
        var targetAssemblies = new Assembly[]
        {
            typeof(Gst.Application).Assembly,
            typeof(GLib.Object).Assembly
        };

        foreach (var asm in targetAssemblies.Distinct())
        {
            try
            {
                NativeLibrary.SetDllImportResolver(asm, MapAndLoad);
            }
            catch (InvalidOperationException)
            {
                // Resolver may already be set for this assembly; ignore.
            }
        }
    }

    private static IntPtr MapAndLoad(string libraryName, Assembly assembly, DllImportSearchPath? dllImportSearchPath)
    {
        string mappedName = null;
        if (mappingDocument == null)
        {
            var resAsm = typeof(NativeLibMapper).Assembly;
            using (var stream = resAsm.GetManifestResourceStream("gstreamer-sharp.dll.config"))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource 'gstreamer-sharp.dll.config' not found in AvaloniaPlayer assembly.");
                mappingDocument = XElement.Load(stream);
            }
        }

        mappedName = MapLibraryName(assembly.Location, libraryName, out mappedName) ? mappedName : libraryName;

        IntPtr handle = IntPtr.Zero;
        if(!NativeLibrary.TryLoad(mappedName, assembly, dllImportSearchPath, out handle))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (mappedName.StartsWith("lib", true, null))
                {
                    NativeLibrary.TryLoad(mappedName.Substring(3), assembly, dllImportSearchPath, out handle);
                }
                else
                {
                    NativeLibrary.TryLoad($"lib{mappedName}", assembly, dllImportSearchPath, out handle);
                }
            }
        }
        return handle;
    }

    private static bool MapLibraryName(string assemblyLocation, string originalLibName, out string mappedLibName)
    {
        mappedLibName = null;
        
        if (mappingDocument == null)
        {
            return false;
        }

        string os = "linux";
        if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            os = "osx";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            os = "windows";
        }

        XElement root = mappingDocument;
        var map =
            (from el in root.Elements("dllmap")
             where (string)el.Attribute("dll") == originalLibName 
             && (string)el.Attribute("os") == os
             select el).FirstOrDefault();

        if (map != null)
            mappedLibName = map.Attribute("target").Value;

        return (mappedLibName != null);
    }
}