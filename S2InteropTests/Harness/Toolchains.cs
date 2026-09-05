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

using System.Globalization;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// A required external toolchain (Python, cargo) is not available on this machine.
    /// The tests are skipped unless the environment variable S2_INTEROP_REQUIRE is set.
    /// </summary>
    public sealed class ToolchainMissingException(String Message) : Exception(Message)
    { }


    /// <summary>
    /// Locates the external toolchains the interoperability tests depend on. Every
    /// location can be overridden with an environment variable:
    /// <list type="bullet">
    ///   <item>S2_INTEROP_PYTHON: the Python interpreter with s2-python installed (skips the venv).</item>
    ///   <item>S2_INTEROP_BASE_PYTHON: the interpreter the venv is created from (3.9 to 3.13).</item>
    ///   <item>S2_INTEROP_VENV: the venv directory (default: &lt;repo&gt;/.venv-s2python).</item>
    ///   <item>S2_INTEROP_CARGO: the cargo executable.</item>
    ///   <item>S2_INTEROP_REQUIRE: "1" makes a missing toolchain a test failure instead of a skip.</item>
    /// </list>
    /// </summary>
    public static class Toolchains
    {

        #region Properties

        /// <summary>
        /// Whether missing toolchains fail the tests instead of skipping them (CI).
        /// </summary>
        public static Boolean Required
            => Environment.GetEnvironmentVariable("S2_INTEROP_REQUIRE") is "1" or "true" or "yes";

        /// <summary>
        /// The Python interpreter of the venv.
        /// </summary>
        public static String VenvPython
            => OperatingSystem.IsWindows()
                   ? Path.Combine(RepositoryPaths.PythonVenv, "Scripts", "python.exe")
                   : Path.Combine(RepositoryPaths.PythonVenv, "bin",     "python");

        #endregion


        #region FindBasePythonAsync()

        /// <summary>
        /// Find an interpreter the s2-python venv can be created from: s2-python 0.10 requires
        /// Python 3.9 to 3.13. Tries S2_INTEROP_BASE_PYTHON, the Windows launcher "py -3.13" …
        /// "py -3.9", and "python3.13" … "python3.9", "python3", "python" on the PATH.
        /// </summary>
        public static async Task<String?> FindBasePythonAsync()
        {

            var candidates  = new List<String[]>();
            var versions    = new[] { "3.13", "3.12", "3.11", "3.10", "3.9" };

            if (Environment.GetEnvironmentVariable("S2_INTEROP_BASE_PYTHON") is { Length: > 0 } configured)
                candidates.Add([ configured ]);

            if (OperatingSystem.IsWindows())
                foreach (var version in versions)
                    candidates.Add([ "py", "-" + version ]);

            foreach (var version in versions)
                candidates.Add([ "python" + version ]);

            candidates.Add([ "python3" ]);
            candidates.Add([ "python"  ]);

            foreach (var candidate in candidates)
            {

                var executable = candidate[0];

                if (!Path.IsPathRooted(executable) && FindOnPath(executable) is null)
                    continue;

                try
                {

                    var probe = await ProcessRunner.RunAsync(
                                    executable,
                                    [ .. candidate.Skip(1), "-c", "import sys; print(sys.executable); print('%d.%d' % sys.version_info[:2])" ],
                                    Timeout: TimeSpan.FromSeconds(30)
                                );

                    if (!probe.IsSuccess)
                        continue;

                    var lines = probe.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (lines.Length < 2)
                        continue;

                    var parts = lines[1].Split('.');
                    if (parts.Length == 2 &&
                        Int32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) &&
                        Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor) &&
                        major == 3 && minor >= 9 && minor <= 13)
                    {
                        return lines[0];
                    }

                }
                catch (Exception)
                {
                    // Not runnable; try the next candidate.
                }

            }

            return null;

        }

        #endregion

        #region FindCargo()

        /// <summary>
        /// Find the cargo executable: S2_INTEROP_CARGO, the PATH, or ~/.cargo/bin.
        /// </summary>
        public static String? FindCargo()
        {

            if (Environment.GetEnvironmentVariable("S2_INTEROP_CARGO") is { Length: > 0 } configured && File.Exists(configured))
                return configured;

            if (FindOnPath("cargo") is { } onPath)
                return onPath;

            var home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var inHome  = Path.Combine(home, ".cargo", "bin", OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");

            return File.Exists(inHome) ? inHome : null;

        }

        #endregion

        #region FindOnPath(FileName)

        /// <summary>
        /// Find an executable on the PATH (on Windows also with the PATHEXT extensions).
        /// </summary>
        /// <param name="FileName">The file name, e.g. "cargo" or "py".</param>
        public static String? FindOnPath(String FileName)
        {

            var path        = Environment.GetEnvironmentVariable("PATH") ?? "";
            var extensions  = OperatingSystem.IsWindows()
                                  ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
                                  : [ "" ];

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {

                if (File.Exists(Path.Combine(directory, FileName)))
                    return Path.Combine(directory, FileName);

                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, FileName + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                        return candidate;
                }

            }

            return null;

        }

        #endregion

    }

}
