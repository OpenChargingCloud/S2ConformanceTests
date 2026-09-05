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

using Microsoft.Extensions.Logging;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// An ILoggerFactory writing everything at or above the given level to the NUnit progress
    /// output, so that the log of WWCP S2 and Hermod (the WebSocket client, the HTTP APIs)
    /// ends up next to the driver output of a test. Enabled per test on demand; the level
    /// can be raised globally with S2_INTEROP_LOG_LEVEL (Trace, Debug, Information, ...).
    /// </summary>
    public sealed class TestLoggerFactory(LogLevel MinimumLevel) : ILoggerFactory
    {

        #region Data

        private static readonly Lazy<LogLevel> configuredLevel = new (() =>
            Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("S2_INTEROP_LOG_LEVEL"), true, out var level)
                ? level
                : LogLevel.Warning);

        #endregion

        #region Properties

        /// <summary>
        /// A factory at the level of S2_INTEROP_LOG_LEVEL (default: Warning).
        /// </summary>
        public static TestLoggerFactory  Default   { get; } = new (configuredLevel.Value);

        /// <summary>
        /// A factory logging everything.
        /// </summary>
        public static TestLoggerFactory  Verbose   { get; } = new (LogLevel.Trace);

        #endregion


        public ILogger CreateLogger(String CategoryName)
            => new TestLogger(CategoryName, MinimumLevel);

        public void AddProvider(ILoggerProvider Provider)
        { }

        public void Dispose()
        { }


        private sealed class TestLogger(String Category, LogLevel MinimumLevel) : ILogger
        {

            public IDisposable? BeginScope<TState>(TState State) where TState : notnull
                => null;

            public Boolean IsEnabled(LogLevel Level)
                => Level >= MinimumLevel && Level != LogLevel.None;

            public void Log<TState>(LogLevel                          Level,
                                    EventId                           EventId,
                                    TState                            State,
                                    Exception?                        Exception,
                                    Func<TState, Exception?, String>  Formatter)
            {

                if (!IsEnabled(Level))
                    return;

                var text = $"[{Level.ToString()[..4].ToLowerInvariant()}] {Category}: {Formatter(State, Exception)}";

                if (Exception is not null)
                    text += Environment.NewLine + Exception;

                TestContext.Progress.WriteLine(text);

            }

        }

    }

}
