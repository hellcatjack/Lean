/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using ProtoBuf.Meta;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Custom.IconicTypes;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine;
using QuantConnect.Logging;
using QuantConnect.Python;
using QuantConnect.Tests;
using QuantConnect.Util;

[assembly: MaintainLogHandler()]
namespace QuantConnect.Tests
{
    [SetUpFixture]
    public class AssemblyInitialize
    {
        private static bool _initialized;

        [OneTimeSetUp]
        public void InitializeTestEnvironment()
        {
            TryAddIconicDataSubTypes();
            AdjustCurrentDirectory();
            var disablePython = Environment.GetEnvironmentVariable("LEAN_DISABLE_PYTHON");
            if (Config.GetBool("lean-disable-python")
                || string.Equals(disablePython, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disablePython, "true", StringComparison.OrdinalIgnoreCase))
            {
                Log.Trace("AssemblyInitialize.InitializeTestEnvironment(): skipping TestGlobals.Initialize because python is disabled.");
                return;
            }
            TestGlobals.Initialize();
        }

        public static void AdjustCurrentDirectory()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            // nunit 3 sets the current folder to a temp folder we need it to be the test bin output folder
            var dir = TestContext.CurrentContext.TestDirectory;
            Environment.CurrentDirectory = dir;
            Directory.SetCurrentDirectory(dir);
            Config.Reset();
            Globals.Reset();

            Log.DebuggingEnabled = Config.GetBool("debug-mode");
            var dataFolderOverride = Environment.GetEnvironmentVariable("LEAN_DATA_FOLDER");
            if (!string.IsNullOrWhiteSpace(dataFolderOverride))
            {
                Config.Set("data-folder", dataFolderOverride);
                Globals.Reset();
            }
            var disablePython = Environment.GetEnvironmentVariable("LEAN_DISABLE_PYTHON");
            if (Config.GetBool("lean-disable-python")
                || string.Equals(disablePython, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disablePython, "true", StringComparison.OrdinalIgnoreCase))
            {
                Log.Trace("AssemblyInitialize.AdjustCurrentDirectory(): Python initialization disabled.");
                return;
            }

            EnsurePythonNetRuntime(Config.Get("python-venv"));

            // Activate virtual environment if defined
            PythonInitializer.ActivatePythonVirtualEnvironment(Config.Get("python-venv"));

            // Initialize and add our Paths
            PythonInitializer.Initialize();
            PythonInitializer.AddPythonPaths(
                new[]
                {
                "./Alphas",
                "./Execution",
                "./Portfolio",
                "./Risk",
                "./Selection",
                "./RegressionAlgorithms",
                "./Research/RegressionScripts",
                "./Python/PandasTests",
                "../../../Algorithm",
                "../../../Algorithm/Selection",
                "../../../Algorithm.Framework",
                "../../../Algorithm.Framework/Selection",
                "../../../Algorithm.Python"
                });
        }

        private static void EnsurePythonNetRuntime(string pythonVenv)
        {
            if (string.IsNullOrWhiteSpace(pythonVenv))
            {
                var fallbackVenv = "/app/stocklean/.venv";
                if (Directory.Exists(fallbackVenv))
                {
                    pythonVenv = fallbackVenv;
                }
                else
                {
                    return;
                }
            }

            var existingDll = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
            if (!string.IsNullOrWhiteSpace(existingDll))
            {
                return;
            }

            try
            {
                var libDir = pythonVenv;
                var pattern = "python*.dll";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    libDir = Path.Combine(pythonVenv, "lib");
                    pattern = "libpython*.dylib";
                }
                else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    libDir = Path.Combine(pythonVenv, "lib");
                    pattern = "libpython*.so*";
                }

                var candidates = Directory.Exists(libDir)
                    ? Directory.GetFiles(libDir, pattern, SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();
                Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
                var dllPath = candidates.Length > 0 ? candidates[^1] : null;
                if (!string.IsNullOrWhiteSpace(dllPath))
                {
                    Environment.SetEnvironmentVariable("PYTHONNET_PYDLL", dllPath);
                }

                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PYTHONHOME")))
                {
                    Environment.SetEnvironmentVariable("PYTHONHOME", pythonVenv);
                }

                if (!string.IsNullOrWhiteSpace(dllPath))
                {
                    Log.Trace($"AssemblyInitialize.EnsurePythonNetRuntime(): PYTHONNET_PYDLL set to {dllPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private static void TryAddIconicDataSubTypes()
        {
            try
            {
                // Loading of custom data types into BaseData as subtypes will be primarily done at runtime.
                RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(1111, typeof(IndexedLinkedData));
                RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(1112, typeof(IndexedLinkedData2));
                RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(1113, typeof(LinkedData));
                RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(1114, typeof(UnlinkedData));
                RuntimeTypeModel.Default[typeof(TradeBar)].AddSubType(1115, typeof(UnlinkedDataTradeBar));
            }
            catch
            {
            }
        }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    public class MaintainLogHandlerAttribute : Attribute, ITestAction
    {
        public static ILogHandler LogHandler { get; private set; }

        public MaintainLogHandlerAttribute()
        {
            LogHandler = LoadLogHandler();
        }

        /// <summary>
        /// Replace the log handler if it has been changed
        /// </summary>
        /// <param name="test"></param>
        public void BeforeTest(ITest test)
        {
            if (Log.LogHandler != LogHandler)
            {
                Log.LogHandler = LogHandler;
            }
        }

        public void AfterTest(ITest test)
        {
            //NOP
        }

        /// <summary>
        /// Set to act on all tests
        /// </summary>
        public ActionTargets Targets => ActionTargets.Test;

        /// <summary>
        /// Load the log handler defined by test context parameters. Defaults to ConsoleLogHandler if no
        /// "log-handler" parameter is found.
        /// </summary>
        /// <returns>An instance of a new LogHandler</returns>
        private static ILogHandler LoadLogHandler()
        {
            if (TestContext.Parameters.Exists("log-handler"))
            {
                var logHandler = TestContext.Parameters["log-handler"];
                Log.Trace($"QuantConnect.Tests.AssemblyInitialize(): Log handler test parameter loaded {logHandler}");

                return Composer.Instance.GetExportedValueByTypeName<ILogHandler>(logHandler);
            }

            // If no parameter just use ConsoleLogHandler
            return new ConsoleLogHandler();
        }
    }
}
