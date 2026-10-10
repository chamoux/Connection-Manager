using System.Runtime.InteropServices;

// Every native DLL this assembly calls is part of Windows: load them from System32 only, never from the app folder or
// the current folder, where a planted copy would run as SYSTEM.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
