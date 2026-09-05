/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the WWCP S2 conformance tests <https://github.com/OpenChargingCloud/S2ConformanceTests>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// The s2-python side: a virtual environment with the s2-python submodule installed
    /// (created on first use) and the driver scripts of tools/s2-python-harness.
    /// </summary>
    public static class PythonHarness
    {

        #region Data

        private static readonly Lock           gate = new ();
        private static          Task<String>?  ready;

        #endregion

        #region Properties

        /// <summary>
        /// The version of s2-python found in the venv (after <see cref="EnsureReadyAsync"/>).
        /// </summary>
        public static String?  S2PythonVersion    { get; private set; }

        #endregion


        #region EnsureReadyAsync()

        /// <summary>
        /// Return the Python interpreter that has s2-python[ws] installed, creating the venv
        /// from the s2-python submodule when needed. Throws <see cref="ToolchainMissingException"/>
        /// when no suitable interpreter exists.
        /// </summary>
        public static Task<String> EnsureReadyAsync()
        {
            lock (gate)
            {
                ready ??= PrepareAsync();
                return ready;
            }
        }

        private static async Task<String> PrepareAsync()
        {

            if (Environment.GetEnvironmentVariable("S2_INTEROP_PYTHON") is { Length: > 0 } configured)
            {

                if (await ProbeAsync(configured))
                    return configured;

                throw new ToolchainMissingException($"S2_INTEROP_PYTHON='{configured}' does not have s2-python[ws] installed!");

            }

            var python = Toolchains.VenvPython;

            if (File.Exists(python) && await ProbeAsync(python))
                return python;

            var basePython = await Toolchains.FindBasePythonAsync()
                                 ?? throw new ToolchainMissingException("No Python 3.9 to 3.13 interpreter was found (s2-python 0.10 does not support 3.14 yet). " +
                                                                        "Install one, or point S2_INTEROP_BASE_PYTHON at it, or S2_INTEROP_PYTHON at an interpreter with s2-python[ws].");

            TestContext.Progress.WriteLine($"[python] creating the venv '{RepositoryPaths.PythonVenv}' from '{basePython}' …");

            var venv = await ProcessRunner.RunAsync(basePython,
                                                    [ "-m", "venv", RepositoryPaths.PythonVenv ],
                                                    Timeout: TimeSpan.FromMinutes(5));

            if (!venv.IsSuccess)
                throw new InvalidOperationException($"Creating the venv failed:{Environment.NewLine}{venv.Output}");

            TestContext.Progress.WriteLine("[python] installing s2-python[ws] from libs/s2-python …");

            var pip = await ProcessRunner.RunAsync(python,
                                                   [ "-m", "pip", "install", "--quiet", "--disable-pip-version-check", "-e", RepositoryPaths.S2Python + "[ws]" ],
                                                   Timeout: TimeSpan.FromMinutes(10));

            if (!pip.IsSuccess)
                throw new InvalidOperationException($"Installing s2-python failed:{Environment.NewLine}{pip.Output}");

            if (!await ProbeAsync(python))
                throw new InvalidOperationException("The venv was created but s2-python[ws] cannot be imported!");

            return python;

        }

        private static async Task<Boolean> ProbeAsync(String Python)
        {

            var probe = await ProcessRunner.RunAsync(Python,
                                                     [ "-c", "import s2python, websockets, pydantic; print(s2python.__version__)" ],
                                                     Timeout: TimeSpan.FromSeconds(60));

            if (!probe.IsSuccess)
                return false;

            S2PythonVersion = probe.StandardOutput.Trim();
            return true;

        }

        #endregion

        #region StartAsync(Script, Arguments)

        /// <summary>
        /// Start a driver script of tools/s2-python-harness (unbuffered, UTF-8).
        /// </summary>
        /// <param name="Script">The script file name, e.g. "s2_echo.py".</param>
        /// <param name="Arguments">The script arguments.</param>
        public static async Task<ExternalProcess> StartAsync(String           Script,
                                                             params String[]  Arguments)
        {

            var python  = await EnsureReadyAsync();
            var script  = Path.Combine(RepositoryPaths.PythonHarness, Script);

            if (!File.Exists(script))
                throw new FileNotFoundException($"The driver script '{script}' does not exist!", script);

            return ExternalProcess.Start(
                       Path.GetFileNameWithoutExtension(Script),
                       python,
                       [ "-u", script, .. Arguments ],
                       RepositoryPaths.PythonHarness,
                       new Dictionary<String, String> {
                           ["PYTHONUNBUFFERED"]   = "1",
                           ["PYTHONIOENCODING"]   = "utf-8",
                           ["PYTHONUTF8"]         = "1"
                       }
                   );

        }

        #endregion

    }

}
