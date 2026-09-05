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
    /// The directories of this repository, located relative to the test assembly: the
    /// repository root is the first directory above the assembly that has a ".gitmodules"
    /// file and a "libs" directory.
    /// </summary>
    public static class RepositoryPaths
    {

        private static readonly Lazy<DirectoryInfo> root = new (FindRoot);

        /// <summary>
        /// The repository root.
        /// </summary>
        public static DirectoryInfo  Root
            => root.Value;

        /// <summary>
        /// The directory of the git submodules.
        /// </summary>
        public static String  Libs
            => Path.Combine(Root.FullName, "libs");

        /// <summary>
        /// The s2-python submodule.
        /// </summary>
        public static String  S2Python
            => Path.Combine(Libs, "s2-python");

        /// <summary>
        /// The s2-rust submodule.
        /// </summary>
        public static String  S2Rust
            => Path.Combine(Libs, "s2-rust");

        /// <summary>
        /// libs/s2auth: the Python implementation of S2 Connect pairing and session initiation.
        /// </summary>
        public static String  S2Auth
            => Path.Combine(Libs, "s2auth");

        /// <summary>
        /// libs/s2-json: the normative S2 JSON schemas.
        /// </summary>
        public static String  S2Json
            => Path.Combine(Libs, "s2-json");

        /// <summary>
        /// libs/s2-connect: the normative S2 Connect OpenAPI files.
        /// </summary>
        public static String  S2Connect
            => Path.Combine(Libs, "s2-connect");

        /// <summary>
        /// libs/s2-documentation: the S2 documentation with the structured data model documentation.
        /// </summary>
        public static String  S2Documentation
            => Path.Combine(Libs, "s2-documentation");

        /// <summary>
        /// libs/WWCP_S2: the stack under test.
        /// </summary>
        public static String  WWCP_S2
            => Path.Combine(Libs, "WWCP_S2");

        /// <summary>
        /// The Python driver scripts (tools/s2-python-harness).
        /// </summary>
        public static String  PythonHarness
            => Path.Combine(Root.FullName, "tools", "s2-python-harness");

        /// <summary>
        /// The Rust driver crate (tools/s2-rust-harness).
        /// </summary>
        public static String  RustHarness
            => Path.Combine(Root.FullName, "tools", "s2-rust-harness");

        /// <summary>
        /// tools/s2auth-harness: the s2auth drivers.
        /// </summary>
        public static String  S2AuthHarness
            => Path.Combine(Root.FullName, "tools", "s2auth-harness");

        /// <summary>
        /// The Python virtual environment with s2-python installed; overridable
        /// with the environment variable S2_INTEROP_VENV.
        /// </summary>
        public static String  PythonVenv
            => Environment.GetEnvironmentVariable("S2_INTEROP_VENV") is { Length: > 0 } venv
                   ? venv
                   : Path.Combine(Root.FullName, ".venv-s2python");

        /// <summary>
        /// The virtual environment s2auth is installed into (S2_INTEROP_S2AUTH_VENV, default .venv-s2auth).
        /// </summary>
        public static String  S2AuthVenv
            => Environment.GetEnvironmentVariable("S2_INTEROP_S2AUTH_VENV") is { Length: > 0 } venv
                   ? venv
                   : Path.Combine(Root.FullName, ".venv-s2auth");


        private static DirectoryInfo FindRoot()
        {

            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {

                if (File.Exists(Path.Combine(directory.FullName, ".gitmodules")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "libs")))
                {
                    return directory;
                }

                directory = directory.Parent;

            }

            throw new InvalidOperationException($"The repository root (a directory with '.gitmodules' and 'libs/') was not found above '{AppContext.BaseDirectory}'!");

        }

    }

}
