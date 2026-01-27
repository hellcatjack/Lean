using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QuantConnect.Tests
{
    internal static class PythonNetTestEnvironmentInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            var venv = Environment.GetEnvironmentVariable("LEAN_PYTHON_VENV") ?? "/app/stocklean/.venv";
            if (!Directory.Exists(venv))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PYTHONHOME")))
            {
                Environment.SetEnvironmentVariable("PYTHONHOME", venv);
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PYTHONNET_PYDLL")))
            {
                return;
            }

            var libDir = venv;
            var pattern = "python*.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                libDir = Path.Combine(venv, "lib");
                pattern = "libpython*.dylib";
            }
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                libDir = Path.Combine(venv, "lib");
                pattern = "libpython*.so*";
            }

            if (!Directory.Exists(libDir))
            {
                return;
            }

            var candidates = Directory.GetFiles(libDir, pattern, SearchOption.TopDirectoryOnly);
            Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
            if (candidates.Length > 0)
            {
                Environment.SetEnvironmentVariable("PYTHONNET_PYDLL", candidates[^1]);
            }
        }
    }
}
