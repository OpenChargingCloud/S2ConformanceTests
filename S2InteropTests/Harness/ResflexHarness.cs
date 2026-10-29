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

#region Usings

using NUnit.Framework;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// The S2 CEM library of the ElaadNL Residential Flexibility initiative
    /// (libs/resflex-open-s2-hems-connector, s2_cem_lib) and the drivers of
    /// tools/resflex-harness, run from their own virtual environment (.venv-resflex, or
    /// S2_INTEROP_RESFLEX_VENV).
    /// </summary>
    /// <remarks>
    /// s2_cem_lib is not installed: its pyproject.toml would put its subpackages at the top
    /// level, so the drivers import it from the submodule checkout, as the engine's own
    /// cem_engine/run.py does. The venv holds its dependencies: s2-python from libs/s2-python
    /// (the pinned one instead of whatever PyPI has, s2_cem_lib asks for 0.9.1 or later),
    /// aiohttp and zeroconf. s2_cem_lib requires Python 3.13 or later.
    ///
    /// Environment:
    /// <list type="bullet">
    ///   <item>S2_INTEROP_RESFLEX_PYTHON: an interpreter that already has the dependencies installed (no venv is created).</item>
    ///   <item>S2_INTEROP_BASE_PYTHON: the interpreter the venv is created from (shared with s2-python and s2auth; it must be 3.13 or 3.14 here).</item>
    ///   <item>S2_INTEROP_RESFLEX_VENV: the venv directory (default: .venv-resflex in the repository root).</item>
    /// </list>
    /// </remarks>
    public static class ResflexHarness
    {

        #region Data

        private static readonly Lock           gate = new ();
        private static          Task<String>?  ready;

        #endregion

        #region Properties

        /// <summary>
        /// The versions of s2_cem_lib and s2-python found in the venv (after <see cref="EnsureReadyAsync"/>).
        /// </summary>
        public static String?  Versions    { get; private set; }

        /// <summary>
        /// The Python interpreter of the resflex venv.
        /// </summary>
        public static String   VenvPython
            => OperatingSystem.IsWindows()
                   ? Path.Combine(RepositoryPaths.ResflexVenv, "Scripts", "python.exe")
                   : Path.Combine(RepositoryPaths.ResflexVenv, "bin",     "python");

        #endregion


        #region EnsureReadyAsync()

        /// <summary>
        /// Return the Python interpreter that can run s2_cem_lib, creating the venv on first use.
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

            if (!Directory.Exists(Path.Combine(RepositoryPaths.Resflex, "s2_cem_lib")))
                throw new ToolchainMissingException($"The resflex-open-s2-hems-connector submodule is not checked out at '{RepositoryPaths.Resflex}': run 'git submodule update --init libs/resflex-open-s2-hems-connector'.");

            if (Environment.GetEnvironmentVariable("S2_INTEROP_RESFLEX_PYTHON") is { Length: > 0 } configured)
            {

                if (await ProbeAsync(configured))
                    return configured;

                throw new ToolchainMissingException($"S2_INTEROP_RESFLEX_PYTHON='{configured}' cannot import s2_cem_lib with its dependencies (s2-python, aiohttp, zeroconf)!");

            }

            var python = VenvPython;

            if (File.Exists(python) && await ProbeAsync(python))
                return python;

            var basePython = await Toolchains.FindBasePythonAsync(MinimumMinor: 13)
                                 ?? throw new ToolchainMissingException("No Python 3.13 or 3.14 interpreter was found for the ElaadNL S2 CEM library (s2_cem_lib requires 3.13). " +
                                                                        "Install one, or point S2_INTEROP_BASE_PYTHON at it, or S2_INTEROP_RESFLEX_PYTHON at an interpreter with its dependencies.");

            TestContext.Progress.WriteLine($"[resflex] creating the venv '{RepositoryPaths.ResflexVenv}' from '{basePython}' …");

            var venv = await ProcessRunner.RunAsync(basePython,
                                                    [ "-m", "venv", RepositoryPaths.ResflexVenv ],
                                                    Timeout: TimeSpan.FromMinutes(5));

            if (!venv.IsSuccess)
                throw new InvalidOperationException($"Creating the resflex venv failed:{Environment.NewLine}{venv.Output}");

            TestContext.Progress.WriteLine("[resflex] installing s2-python from libs/s2-python, aiohttp and zeroconf …");

            // The lower bounds are the ones of s2_cem_lib/pyproject.toml.
            var pip = await ProcessRunner.RunAsync(python,
                                                   [ "-m", "pip", "install", "--quiet", "--disable-pip-version-check",
                                                     "-e", RepositoryPaths.S2Python,
                                                     "aiohttp>=3.13.3",
                                                     "zeroconf>=0.148.0" ],
                                                   Timeout: TimeSpan.FromMinutes(15));

            if (!pip.IsSuccess)
                throw new InvalidOperationException($"Installing the dependencies of s2_cem_lib failed:{Environment.NewLine}{pip.Output}");

            if (!await ProbeAsync(python))
                throw new InvalidOperationException("The resflex venv was created but s2_cem_lib cannot be imported from it!");

            return python;

        }

        private static async Task<Boolean> ProbeAsync(String Python)
        {

            var probe = await ProcessRunner.RunAsync(Python,
                                                     [ "-c",
                                                       "import sys, tomllib; " +
                                                       "sys.path.insert(0, sys.argv[1]); " +
                                                       "import s2_cem_lib, aiohttp, zeroconf; " +
                                                       "from importlib.metadata import version; " +
                                                       "lib = tomllib.load(open(sys.argv[1] + '/s2_cem_lib/pyproject.toml', 'rb'))['project']['version']; " +
                                                       "print('s2_cem_lib %s, s2-python %s' % (lib, version('s2-python')))",
                                                       RepositoryPaths.Resflex ],
                                                     Timeout: TimeSpan.FromSeconds(60));

            if (!probe.IsSuccess)
                return false;

            Versions = probe.StandardOutput.Trim();
            return true;

        }

        #endregion

        #region StartAsync(Script, Arguments)

        /// <summary>
        /// Start one of the resflex drivers (tools/resflex-harness/*.py) with the given arguments.
        /// </summary>
        public static async Task<ExternalProcess> StartAsync(String           Script,
                                                             params String[]  Arguments)
        {

            var python  = await EnsureReadyAsync();
            var script  = Path.Combine(RepositoryPaths.ResflexHarness, Script);

            if (!File.Exists(script))
                throw new FileNotFoundException($"The driver script '{script}' does not exist!", script);

            return ExternalProcess.Start(
                       Path.GetFileNameWithoutExtension(Script),
                       python,
                       [ "-u", script, "--library", RepositoryPaths.Resflex, .. Arguments ],
                       RepositoryPaths.ResflexHarness,
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
