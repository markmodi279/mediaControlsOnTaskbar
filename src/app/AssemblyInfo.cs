using System.Reflection;

// Assembly identity. csc reads these straight out of source and stamps the Win32 version
// resource from them - there is no project file and no SDK here (see CLAUDE.md).
//
// ASCII only, like every other source file: the in-box compiler reads BOM-less files in
// the system codepage, and this text ends up in the shell's UI.

[assembly: AssemblyTitle("MediaController")]
[assembly: AssemblyProduct("MediaController")]
[assembly: AssemblyDescription("Now-playing and transport controls docked in the Windows 10 taskbar.")]
[assembly: AssemblyCompany("Mark")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mark")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
