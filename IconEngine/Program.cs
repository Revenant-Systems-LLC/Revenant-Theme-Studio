using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    static void Main(string[] args)
    {
        Console.WriteLine("Revenant Icon Test");

        string iconFolder = @"C:\The-Ossuary\Revenant-Systems\Projects\Revenant-Theme-Studio\IconEngine\test-data\folder-icons";
        string targetFolder = @"C:\TestFolder";
        string iniPath = Path.Combine(targetFolder, "desktop.ini");

        string[] icons = Directory.GetFiles(iconFolder, "*.ico");
        Console.WriteLine($"Found {icons.Length} icons.");

        string selectedIcon = Array.Find(icons, i => Path.GetFileName(i) == "folder-icons.ico") ?? icons[0];
        Console.WriteLine($"Using: {Path.GetFileName(selectedIcon)}");

        // Strip attributes on desktop.ini if it exists so we can overwrite
        if (File.Exists(iniPath))
            File.SetAttributes(iniPath, FileAttributes.Normal);

        // Strip ReadOnly on folder so we can modify it
        File.SetAttributes(targetFolder, File.GetAttributes(targetFolder) & ~FileAttributes.ReadOnly);

        // Write desktop.ini
        string content = "[.ShellClassInfo]\r\nIconResource=" + selectedIcon + ",0\r\n";
        File.WriteAllText(iniPath, content, Encoding.Unicode);

        // Re-apply Hidden + System on desktop.ini
        File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);

        // Re-apply ReadOnly on folder
        File.SetAttributes(targetFolder, File.GetAttributes(targetFolder) | FileAttributes.ReadOnly);

        // Notify shell of the change
		IntPtr folderPtr = 
	    Marshal.StringToHGlobalAuto(targetFolder);
		SHChangeNotify(0x00002000, 0x0001, folderPtr, IntPtr.Zero);
		// 0x0001 = SHCNF_PATH - targets by path, not global
		Marshal.FreeHGlobal(folderPtr);

    }
}
