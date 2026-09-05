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
    /// s2auth (libs/s2auth), the Python implementation of S2 Connect pairing and session
    /// initiation, installed with its client and server extras into its own virtual
    /// environment (.venv-s2auth, or S2_INTEROP_S2AUTH_VENV), and the drivers of
    /// tools/s2auth-harness started from it.
    /// </summary>
    /// <remarks>
    /// Environment:
    /// <list type="bullet">
    ///   <item>S2_INTEROP_S2AUTH_PYTHON: an interpreter that already has s2auth[client,server] installed (no venv is created).</item>
    ///   <item>S2_INTEROP_BASE_PYTHON: the interpreter the venv is created from (shared with s2-python).</item>
    ///   <item>S2_INTEROP_S2AUTH_VENV: the venv directory (default: .venv-s2auth in the repository root).</item>
    /// </list>
    /// </remarks>
    public static class S2AuthHarness
    {

        #region Data

        private static readonly Lock           gate = new ();
        private static          Task<String>?  ready;

        #endregion

        #region Properties

        /// <summary>
        /// The version of s2auth found in the venv (after <see cref="EnsureReadyAsync"/>).
        /// </summary>
        public static String?  S2AuthVersion    { get; private set; }

        /// <summary>
        /// The Python interpreter of the s2auth venv.
        /// </summary>
        public static String   VenvPython
            => OperatingSystem.IsWindows()
                   ? Path.Combine(RepositoryPaths.S2AuthVenv, "Scripts", "python.exe")
                   : Path.Combine(RepositoryPaths.S2AuthVenv, "bin",     "python");

        #endregion


        #region EnsureReadyAsync()

        /// <summary>
        /// Return the Python interpreter that has s2auth[client,server] installed, creating
        /// the venv from libs/s2auth on first use.
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

            if (Environment.GetEnvironmentVariable("S2_INTEROP_S2AUTH_PYTHON") is { Length: > 0 } configured)
            {

                if (await ProbeAsync(configured))
                    return configured;

                throw new ToolchainMissingException($"S2_INTEROP_S2AUTH_PYTHON='{configured}' does not have s2auth[client,server] installed!");

            }

            var python = VenvPython;

            if (File.Exists(python) && await ProbeAsync(python))
                return python;

            if (!Directory.Exists(Path.Combine(RepositoryPaths.S2Auth, "src", "s2auth")))
                throw new ToolchainMissingException($"The s2auth submodule is not checked out at '{RepositoryPaths.S2Auth}': run 'git submodule update --init libs/s2auth'.");

            var basePython = await Toolchains.FindBasePythonAsync()
                                 ?? throw new ToolchainMissingException("No Python 3.10 to 3.13 interpreter was found for s2auth. " +
                                                                        "Install one, or point S2_INTEROP_BASE_PYTHON at it, or S2_INTEROP_S2AUTH_PYTHON at an interpreter with s2auth[client,server].");

            TestContext.Progress.WriteLine($"[s2auth] creating the venv '{RepositoryPaths.S2AuthVenv}' from '{basePython}' …");

            var venv = await ProcessRunner.RunAsync(basePython,
                                                    [ "-m", "venv", RepositoryPaths.S2AuthVenv ],
                                                    Timeout: TimeSpan.FromMinutes(5));

            if (!venv.IsSuccess)
                throw new InvalidOperationException($"Creating the s2auth venv failed:{Environment.NewLine}{venv.Output}");

            TestContext.Progress.WriteLine("[s2auth] installing s2auth[client,server] from libs/s2auth …");

            // s2auth builds with poetry-core and poetry-dynamic-versioning; an editable install
            // keeps the submodule checkout authoritative. A plain install is the fallback when
            // the dynamic versioning cannot see the git metadata of the submodule.
            var pip = await ProcessRunner.RunAsync(python,
                                                   [ "-m", "pip", "install", "--quiet", "--disable-pip-version-check", "-e", RepositoryPaths.S2Auth + "[client,server]" ],
                                                   Timeout: TimeSpan.FromMinutes(15));

            if (!pip.IsSuccess)
            {

                TestContext.Progress.WriteLine($"[s2auth] the editable install failed, trying a plain install:{Environment.NewLine}{pip.Output}");

                pip = await ProcessRunner.RunAsync(python,
                                                   [ "-m", "pip", "install", "--quiet", "--disable-pip-version-check", RepositoryPaths.S2Auth + "[client,server]" ],
                                                   Timeout: TimeSpan.FromMinutes(15));

                if (!pip.IsSuccess)
                    throw new InvalidOperationException($"Installing s2auth failed:{Environment.NewLine}{pip.Output}");

            }

            if (!await ProbeAsync(python))
                throw new InvalidOperationException("The s2auth venv was created but s2auth[client,server] cannot be imported!");

            return python;

        }

        private static async Task<Boolean> ProbeAsync(String Python)
        {

            var probe = await ProcessRunner.RunAsync(Python,
                                                     [ "-c", "import s2auth, s2auth.client, s2auth.server, httpx, fastapi, uvicorn, sqlalchemy; print(getattr(s2auth, '__version__', 'unknown'))" ],
                                                     Timeout: TimeSpan.FromSeconds(60));

            if (!probe.IsSuccess)
                return false;

            S2AuthVersion = probe.StandardOutput.Trim();
            return true;

        }

        #endregion

        #region StartAsync(Script, Arguments)

        /// <summary>
        /// Start one of the s2auth drivers (tools/s2auth-harness/*.py) with the given arguments.
        /// </summary>
        public static async Task<ExternalProcess> StartAsync(String           Script,
                                                             params String[]  Arguments)
        {

            var python  = await EnsureReadyAsync();
            var script  = Path.Combine(RepositoryPaths.S2AuthHarness, Script);

            if (!File.Exists(script))
                throw new FileNotFoundException($"The driver script '{script}' does not exist!", script);

            return ExternalProcess.Start(
                       Path.GetFileNameWithoutExtension(Script),
                       python,
                       [ "-u", script, .. Arguments ],
                       RepositoryPaths.S2AuthHarness,
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
