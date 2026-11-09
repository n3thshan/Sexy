using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Sexy
{
    /// <summary>
    /// Windowless launcher for SexyControl.ps1 and Sexy.ps1.
    ///
    /// powershell.exe is a console program, and on Windows 11 the default
    /// terminal host (Windows Terminal) ignores -WindowStyle Hidden, so
    /// starting it from a console leaves a blank terminal window behind.
    /// Starting it from a GUI-subsystem process with CreateNoWindow = true
    /// means no console is created at all.
    ///
    /// Usage:
    ///   SexyLauncher.exe                  -> opens SexyControl.ps1
    ///   SexyLauncher.exe -Action Auto     -> runs Sexy.ps1
    ///                                          and returns its exit code
    ///
    /// Built with the inbox compiler:
    ///   csc.exe /nologo /target:winexe /out:SexyLauncher.exe /r:System.dll SexyLauncher.cs
    /// </summary>
    internal static class Launcher1
    {
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string psExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");

            bool gui = args.Length == 0;
            string script = Path.Combine(baseDir, gui ? "SexyControl.ps1" : "Sexy.ps1");

            if (!File.Exists(script))
            {
                return 2;
            }

            StringBuilder arguments = new StringBuilder();
            arguments.Append("-NoProfile ");
            if (!gui)
            {
                arguments.Append("-NonInteractive ");
            }
            arguments.Append("-ExecutionPolicy Bypass -WindowStyle Hidden -File ");
            arguments.Append(Quote(script));

            foreach (string arg in args)
            {
                arguments.Append(' ');
                arguments.Append(Quote(arg));
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = psExe;
            psi.Arguments = arguments.ToString();
            psi.WorkingDirectory = baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            using (Process process = Process.Start(psi))
            {
                if (process == null)
                {
                    return 3;
                }

                if (gui)
                {
                    // The window keeps running on its own.
                    return 0;
                }

                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
