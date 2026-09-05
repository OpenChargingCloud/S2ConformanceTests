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

using System.Net;
using System.Net.Sockets;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// The s2-rust side: the driver crate tools/s2-rust-harness (built with cargo on first use,
    /// release profile) whose binaries wrap the s2energy crates of libs/s2-rust.
    ///
    /// The message-layer driver (s2-json-echo) builds everywhere. The S2 Connect drivers depend
    /// on s2energy-connection, which only compiles on Unix (it uses tokio::net::unix), so on
    /// Windows they are built and started inside WSL: the repository is reached through
    /// /mnt/&lt;drive&gt;/…, the build output lives in the WSL home directory, servers started in
    /// WSL are reached from Windows via localhost, and WSL reaches Windows servers through the
    /// default gateway of the WSL virtual network. Environment variables:
    /// <list type="bullet">
    ///   <item>S2_INTEROP_CARGO: the cargo executable (native builds).</item>
    ///   <item>S2_INTEROP_WSL_DISTRO: the WSL distribution (default: the default distribution).</item>
    ///   <item>S2_INTEROP_SKIP_CARGO_BUILD: "1" uses previously built binaries as they are.</item>
    ///   <item>S2_INTEROP_RUST_LOG: the RUST_LOG filter of the drivers (default: info).</item>
    /// </list>
    /// </summary>
    public static class RustHarness
    {

        #region Data

        /// <summary>
        /// The drivers that need s2energy-connection (Unix only; WSL on Windows).
        /// </summary>
        private static readonly HashSet<String>  connectDrivers  = [ "s2-hmac", "s2-pairing-server", "s2-pairing-client", "s2-comm-server", "s2-comm-client" ];

        private static readonly Lock                    gate = new ();
        private static          Task<String>?           nativeBuild;
        private static          Task<WSLEnvironment>?   wslBuild;

        #endregion

        #region (record) WSLEnvironment

        /// <summary>
        /// The WSL distribution the S2 Connect drivers run in.
        /// </summary>
        /// <param name="Distribution">The distribution name.</param>
        /// <param name="Home">The Linux home directory.</param>
        /// <param name="ReleaseDirectory">The Linux directory with the built drivers.</param>
        /// <param name="HostAddress">The address under which WSL reaches the Windows host.</param>
        public sealed record WSLEnvironment(String  Distribution,
                                            String  Home,
                                            String  ReleaseDirectory,
                                            String  HostAddress);

        #endregion

        #region Properties

        /// <summary>
        /// Whether the given driver runs inside WSL on this machine.
        /// </summary>
        /// <param name="Binary">The driver name, e.g. "s2-pairing-server".</param>
        public static Boolean RunsInWSL(String Binary)
            => OperatingSystem.IsWindows() && connectDrivers.Contains(Binary);

        #endregion


        #region EnsureBuiltAsync(Binary = null)

        /// <summary>
        /// Build the drivers once per test run: natively (with the S2 Connect drivers on Unix)
        /// and, on Windows for an S2 Connect driver, inside WSL. Throws
        /// <see cref="ToolchainMissingException"/> when cargo (or WSL with cargo) is missing;
        /// a failing build is a test error.
        /// </summary>
        /// <param name="Binary">The driver that is going to be used (default: the message-layer drivers).</param>
        public static async Task EnsureBuiltAsync(String? Binary = null)
        {
            if (Binary is not null && RunsInWSL(Binary))
                await EnsureWSLBuiltAsync();
            else
                await EnsureNativeBuiltAsync();
        }

        private static Task<String> EnsureNativeBuiltAsync()
        {
            lock (gate)
            {
                nativeBuild ??= BuildNativeAsync();
                return nativeBuild;
            }
        }

        private static Task<WSLEnvironment> EnsureWSLBuiltAsync()
        {
            lock (gate)
            {
                wslBuild ??= BuildInWSLAsync();
                return wslBuild;
            }
        }

        private static async Task<String> BuildNativeAsync()
        {

            var targetDirectory = Path.Combine(RepositoryPaths.RustHarness, "target", "release");

            if (Environment.GetEnvironmentVariable("S2_INTEROP_SKIP_CARGO_BUILD") is "1" or "true" && Directory.Exists(targetDirectory))
                return targetDirectory;

            var cargo = Toolchains.FindCargo()
                            ?? throw new ToolchainMissingException("cargo was not found (PATH, ~/.cargo/bin or S2_INTEROP_CARGO). Install Rust via https://rustup.rs to run the s2-rust tests.");

            var arguments = new List<String> { "build", "--release", "--manifest-path", Path.Combine(RepositoryPaths.RustHarness, "Cargo.toml") };

            if (!OperatingSystem.IsWindows())
                arguments.AddRange([ "--features", "connect" ]);

            TestContext.Progress.WriteLine($"[cargo] building tools/s2-rust-harness with '{cargo}' …");

            var build = await ProcessRunner.RunAsync(cargo, arguments, RepositoryPaths.RustHarness, TimeSpan.FromMinutes(30));

            if (!build.IsSuccess)
                throw new InvalidOperationException($"cargo build of the s2-rust harness failed:{Environment.NewLine}{build.Output}");

            return targetDirectory;

        }

        private static async Task<WSLEnvironment> BuildInWSLAsync()
        {

            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("WSL builds are only used on Windows!");

            var wsl = Toolchains.FindOnPath("wsl")
                          ?? throw new ToolchainMissingException("s2energy-connection (S2 Connect) only builds on Unix; on Windows the S2 Connect tests need WSL, but wsl.exe was not found.");

            var distribution  = Environment.GetEnvironmentVariable("S2_INTEROP_WSL_DISTRO");
            var distroArgs    = distribution is { Length: > 0 } ? new[] { "-d", distribution } : [];

            var probe = await ProcessRunner.RunAsync(wsl, [ .. distroArgs, "-e", "sh", "-c", "echo $WSL_DISTRO_NAME; echo $HOME; test -x $HOME/.cargo/bin/cargo && echo cargo-ok; ip route show default | cut -d' ' -f3" ],
                                                     Timeout: TimeSpan.FromSeconds(60));

            if (!probe.IsSuccess)
                throw new ToolchainMissingException($"s2energy-connection (S2 Connect) only builds on Unix; on Windows the S2 Connect tests need a WSL distribution with Rust, but WSL did not start:{Environment.NewLine}{probe.Output}");

            var lines = probe.StandardOutput.Replace("\0", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length < 2)
                throw new ToolchainMissingException($"Unexpected WSL probe output:{Environment.NewLine}{probe.Output}");

            var distro    = lines[0];
            var home      = lines[1];
            var hasCargo  = lines.Contains("cargo-ok");
            var hostIP    = lines.Skip(2).FirstOrDefault(line => line != "cargo-ok" && IPAddress.TryParse(line, out _)) ?? "";

            if (!hasCargo)
                throw new ToolchainMissingException($"Rust is not installed inside the WSL distribution '{distro}'. Run \"curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal\" there.");

            var releaseDirectory  = $"{home}/.cache/s2-rust-harness/target/release";
            var environment       = new WSLEnvironment(distro, home, releaseDirectory, hostIP);

            if (Environment.GetEnvironmentVariable("S2_INTEROP_SKIP_CARGO_BUILD") is "1" or "true")
                return environment;

            TestContext.Progress.WriteLine($"[cargo/wsl] building tools/s2-rust-harness in WSL '{distro}' …");

            var script = $"cd {ToWSLPath(RepositoryPaths.RustHarness)} && CARGO_TARGET_DIR=$HOME/.cache/s2-rust-harness/target $HOME/.cargo/bin/cargo build --release --features connect";

            var build = await ProcessRunner.RunAsync(wsl, [ "-d", distro, "-e", "sh", "-c", script ], Timeout: TimeSpan.FromMinutes(30));

            if (!build.IsSuccess)
                throw new InvalidOperationException($"cargo build of the s2-rust harness in WSL failed:{Environment.NewLine}{build.Output}");

            return environment;

        }

        #endregion

        #region WSLAsync()

        /// <summary>
        /// The WSL environment (after the build), for tests that need the host address.
        /// </summary>
        public static Task<WSLEnvironment> WSLAsync()
            => EnsureWSLBuiltAsync();

        #endregion

        #region ToWSLPath(WindowsPath)

        /// <summary>
        /// Map a Windows path to its WSL mount path ("C:\a\b" → "/mnt/c/a/b").
        /// </summary>
        /// <param name="WindowsPath">An absolute Windows path.</param>
        public static String ToWSLPath(String WindowsPath)
        {

            var full = Path.GetFullPath(WindowsPath);

            if (full.Length < 2 || full[1] != ':')
                throw new ArgumentException($"'{WindowsPath}' is not an absolute Windows path!", nameof(WindowsPath));

            return "/mnt/" + Char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');

        }

        #endregion

        #region PathForDriver(Binary, WindowsPath)

        /// <summary>
        /// The form of a local file path a driver understands (a WSL mount path when the
        /// driver runs inside WSL).
        /// </summary>
        /// <param name="Binary">The driver.</param>
        /// <param name="WindowsPath">The local path.</param>
        public static String PathForDriver(String  Binary,
                                           String  WindowsPath)
            => RunsInWSL(Binary)
                   ? ToWSLPath(WindowsPath)
                   : WindowsPath;

        #endregion


        #region StartAsync(Binary, Arguments)

        /// <summary>
        /// Start a driver binary of tools/s2-rust-harness (inside WSL when needed).
        /// </summary>
        /// <param name="Binary">The binary name, e.g. "s2-json-echo".</param>
        /// <param name="Arguments">The arguments.</param>
        public static async Task<ExternalProcess> StartAsync(String           Binary,
                                                             params String[]  Arguments)
        {

            var (fileName, arguments, workingDirectory) = await CommandLineAsync(Binary, Arguments);

            return ExternalProcess.Start(
                       Binary,
                       fileName,
                       arguments,
                       workingDirectory,
                       new Dictionary<String, String> {
                           ["RUST_LOG"]         = LogLevel,
                           ["RUST_BACKTRACE"]   = "1"
                       }
                   );

        }

        #endregion

        #region RunAsync(Binary, Arguments)

        /// <summary>
        /// Run a driver binary to completion (inside WSL when needed).
        /// </summary>
        /// <param name="Binary">The binary name, e.g. "s2-hmac".</param>
        /// <param name="Arguments">The arguments.</param>
        public static async Task<ProcessResult> RunAsync(String           Binary,
                                                         params String[]  Arguments)
        {

            var (fileName, arguments, workingDirectory) = await CommandLineAsync(Binary, Arguments);

            return await ProcessRunner.RunAsync(fileName,
                                                arguments,
                                                workingDirectory,
                                                TimeSpan.FromSeconds(60),
                                                new Dictionary<String, String> {
                                                    ["RUST_LOG"]         = LogLevel,
                                                    ["RUST_BACKTRACE"]   = "1"
                                                });

        }

        #endregion

        #region (private) CommandLineAsync(Binary, Arguments) / LogLevel

        private static String LogLevel
            => Environment.GetEnvironmentVariable("S2_INTEROP_RUST_LOG") ?? "info";

        private static async Task<(String FileName, List<String> Arguments, String WorkingDirectory)> CommandLineAsync(String                Binary,
                                                                                                                       IEnumerable<String>   Arguments)
        {

            if (RunsInWSL(Binary))
            {

                var wsl  = await EnsureWSLBuiltAsync();
                var exe  = Toolchains.FindOnPath("wsl") ?? "wsl.exe";

                return (exe,
                        [ "-d", wsl.Distribution,
                          "--cd", ToWSLPath(RepositoryPaths.RustHarness),
                          "-e", "env", "RUST_LOG=" + LogLevel, "RUST_BACKTRACE=1",
                          $"{wsl.ReleaseDirectory}/{Binary}",
                          .. Arguments ],
                        RepositoryPaths.RustHarness);

            }

            var directory  = await EnsureNativeBuiltAsync();
            var path       = Path.Combine(directory, OperatingSystem.IsWindows() ? Binary + ".exe" : Binary);

            if (!File.Exists(path))
                throw new FileNotFoundException($"The driver binary '{path}' was not built!", path);

            return (path, [.. Arguments], RepositoryPaths.RustHarness);

        }

        #endregion


        #region HostAddressForDriverAsync(Binary)

        /// <summary>
        /// The address under which a driver reaches servers of this test process:
        /// the WSL host address for drivers inside WSL, loopback otherwise.
        /// </summary>
        /// <param name="Binary">The driver.</param>
        public static async Task<String> HostAddressForDriverAsync(String Binary)
        {

            if (!RunsInWSL(Binary))
                return "127.0.0.1";

            var wsl = await EnsureWSLBuiltAsync();

            if (String.IsNullOrEmpty(wsl.HostAddress))
                throw new ToolchainMissingException("The Windows host address could not be determined from inside WSL (no default route).");

            return wsl.HostAddress;

        }

        #endregion

        #region EnsureHostAliasAsync(Binary, HostName)

        /// <summary>
        /// Make the given host name resolve to this process for a driver: inside WSL an entry
        /// in /etc/hosts (written as root, which WSL grants without a password; WSL regenerates
        /// the file on restart) pointing at the Windows host address; natively the name must
        /// already resolve to loopback. Returns false when the alias could not be established.
        /// </summary>
        /// <param name="Binary">The driver.</param>
        /// <param name="HostName">The host name, e.g. "wwcp-cem.local".</param>
        public static async Task<Boolean> EnsureHostAliasAsync(String  Binary,
                                                               String  HostName)
        {

            if (!RunsInWSL(Binary))
                return true;

            var wsl     = await EnsureWSLBuiltAsync();
            var script  = $"grep -q '[[:space:]]{HostName}\\b' /etc/hosts && grep -q '{wsl.HostAddress}[[:space:]]*{HostName}' /etc/hosts || " +
                          $"(sed -i '/{HostName}/d' /etc/hosts && printf '%s %s # S2ConformanceTests\\n' {wsl.HostAddress} {HostName} >> /etc/hosts); " +
                          $"grep '{HostName}' /etc/hosts";

            var result  = await ProcessRunner.RunAsync(Toolchains.FindOnPath("wsl") ?? "wsl.exe",
                                                       [ "-d", wsl.Distribution, "-u", "root", "-e", "sh", "-c", script ],
                                                       Timeout: TimeSpan.FromSeconds(30));

            if (!result.IsSuccess || !result.StandardOutput.Contains(HostName, StringComparison.Ordinal))
            {
                TestContext.Progress.WriteLine($"[wsl] could not map {HostName} to {wsl.HostAddress} in /etc/hosts of '{wsl.Distribution}': {result.Output}");
                return false;
            }

            TestContext.Progress.WriteLine($"[wsl] {result.StandardOutput.Trim()}");
            return true;

        }

        #endregion

        #region CanReachThisProcessAsync(Binary)

        /// <summary>
        /// Whether a driver can open TCP connections to servers of this test process (inside
        /// WSL this needs the Windows firewall to admit the test host on the WSL network).
        /// </summary>
        /// <param name="Binary">The driver.</param>
        public static async Task<Boolean> CanReachThisProcessAsync(String Binary)
        {

            if (!RunsInWSL(Binary))
                return true;

            var wsl       = await EnsureWSLBuiltAsync();
            var listener  = new TcpListener(IPAddress.Any, 0);

            listener.Start();

            try
            {

                var port    = ((IPEndPoint) listener.LocalEndpoint).Port;
                var accept  = listener.AcceptTcpClientAsync();

                var script  = $"import socket\ntry:\n    socket.create_connection(('{wsl.HostAddress}', {port}), timeout=3).close()\n    print('reachable')\nexcept Exception as error:\n    print('unreachable:', error)\n";

                var probe   = await ProcessRunner.RunAsync(Toolchains.FindOnPath("wsl") ?? "wsl.exe",
                                                           [ "-d", wsl.Distribution, "-e", "python3", "-c", script ],
                                                           Timeout: TimeSpan.FromSeconds(15));

                if (probe.StandardOutput.Contains("reachable", StringComparison.Ordinal))
                {
                    try
                    {
                        using var client = await accept.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception)
                    {
                        // The probe connected and went away already.
                    }
                    return true;
                }

                TestContext.Progress.WriteLine($"[wsl] the WSL distribution cannot reach this process at {wsl.HostAddress}:{port}: {probe.Output}");
                return false;

            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

    }

}
