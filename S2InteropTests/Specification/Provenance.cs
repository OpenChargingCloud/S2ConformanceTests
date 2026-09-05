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

using System.Text.RegularExpressions;

using NUnit.Framework;

using cloud.charging.open.protocols.S2.InteropTests.Harness;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Specification
{

    /// <summary>
    /// The provenance WWCP S2 records for its embedded copies of upstream material:
    /// THIRD-PARTY-NOTICES.md names, per upstream repository, the commit the copy was taken
    /// from. That commit is compared with the submodule pinned here.
    /// </summary>
    public static partial class Provenance
    {

        #region Data

        private static String NoticesFile
            => Path.Combine(RepositoryPaths.WWCP_S2, "THIRD-PARTY-NOTICES.md");

        [GeneratedRegex(@"\(commit `([0-9a-f]{7,40})`\)")]
        private static partial Regex CommitPattern();

        #endregion


        #region CommitNamedInNotices(SectionTitle)

        /// <summary>
        /// The commit named in the section of THIRD-PARTY-NOTICES.md whose heading starts
        /// with the given title.
        /// </summary>
        public static String CommitNamedInNotices(String SectionTitle)
        {

            if (!File.Exists(NoticesFile))
                Assert.Fail($"'{NoticesFile}' does not exist!");

            var lines    = File.ReadAllLines(NoticesFile);
            var start    = Array.FindIndex(lines, line => line.StartsWith("## " + SectionTitle, StringComparison.Ordinal));

            if (start < 0)
                Assert.Fail($"THIRD-PARTY-NOTICES.md has no section '## {SectionTitle}'!");

            var section  = lines.Skip(start + 1).TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal));
            var match    = section.Select(line => CommitPattern().Match(line)).FirstOrDefault(m => m.Success);

            if (match is null)
                Assert.Fail($"The section '## {SectionTitle}' of THIRD-PARTY-NOTICES.md names no upstream commit (expected '(commit `<sha>`)')!");

            return match!.Groups[1].Value;

        }

        #endregion

        #region PinnedCommitAsync(SubmoduleDirectory)

        /// <summary>
        /// The commit the given submodule is checked out at (git rev-parse HEAD); null when
        /// git is not available or the directory is not a git checkout.
        /// </summary>
        public static async Task<String?> PinnedCommitAsync(String SubmoduleDirectory)
        {

            var git = Toolchains.FindOnPath("git");

            if (git is null)
                return null;

            var result = await ProcessRunner.RunAsync(git,
                                                      [ "-C", SubmoduleDirectory, "rev-parse", "HEAD" ],
                                                      Timeout: TimeSpan.FromSeconds(30));

            if (!result.IsSuccess)
            {
                TestContext.Progress.WriteLine($"'git -C {SubmoduleDirectory} rev-parse HEAD' failed (not a git checkout?):{Environment.NewLine}{result.Output}");
                return null;
            }

            return result.StandardOutput.Trim();

        }

        #endregion

        #region AssertNoticeNamesThePinnedCommitAsync(SectionTitle, SubmoduleDirectory)

        public static async Task AssertNoticeNamesThePinnedCommitAsync(String SectionTitle, String SubmoduleDirectory)
        {

            var documented  = CommitNamedInNotices(SectionTitle);
            var pinned      = await PinnedCommitAsync(SubmoduleDirectory);

            if (pinned is null)
                Assert.Ignore("git was not found on the PATH or the submodule is not a git checkout; the pinned commit cannot be determined.");

            Assert.That(pinned, Does.StartWith(documented),
                        $"THIRD-PARTY-NOTICES.md of WWCP S2 says its copy was taken from commit {documented}, " +
                        $"but the submodule '{Path.GetFileName(SubmoduleDirectory)}' is pinned at {pinned}. " +
                        "Either upstream moved (refresh the copy and the notice in WWCP S2, then bump the pin here) " +
                        "or the pin was bumped without the copy following.");

        }

        #endregion

    }

}
