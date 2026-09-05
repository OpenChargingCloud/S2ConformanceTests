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

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Specification
{

    /// <summary>
    /// WWCP S2 embeds the four S2 Connect v1.0 OpenAPI files (flexiblepower/s2-connect:
    /// common, pairing, session initiation, WAN endpoint registry), which its data structures
    /// quote. The normative files are pinned here as the submodule libs/s2-connect. These tests
    /// compare the embedded copy with the pin as a set and line by line (line endings and
    /// trailing white space aside), so that an API change upstream is reported by
    /// upstream-drift.yml the night it lands.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Specification)]
    public class S2ConnectOpenAPIDriftTests
    {

        #region Data

        private const String Marker = "S2Connect.v1._0.";

        private static String UpstreamOpenAPI
            => Path.Combine(RepositoryPaths.S2Connect, "openapi");

        #endregion


        #region (private) Helpers

        private static void RequireUpstream()
        {
            if (!Directory.Exists(UpstreamOpenAPI))
                Assert.Fail($"The s2-connect submodule is not checked out at '{RepositoryPaths.S2Connect}': run 'git submodule update --init libs/s2-connect'.");
        }

        private static String FileNameOf(String ResourceName)
        {
            var index = ResourceName.IndexOf(Marker, StringComparison.Ordinal);
            return index < 0
                       ? ResourceName
                       : ResourceName[(index + Marker.Length)..];
        }

        private static String[] NormalizedLines(String Text)
            => Text.Replace("\r\n", "\n").
                    Split('\n').
                    Select(line => line.TrimEnd()).
                    Reverse().
                    SkipWhile(String.IsNullOrEmpty).
                    Reverse().
                    ToArray();

        #endregion


        #region OpenAPIFiles_MatchTheUpstreamFiles()

        [Test]
        [S2C("Schemas.S2Connect.OpenAPI.Drift")]
        public void OpenAPIFiles_MatchTheUpstreamFiles()
        {

            RequireUpstream();

            var resources  = EmbeddedSchemas.OpenAPIFileNames.ToDictionary(FileNameOf, name => name, StringComparer.Ordinal);
            var upstream   = Directory.GetFiles(UpstreamOpenAPI, "*.yml").
                                       Select(Path.GetFileName).
                                       Cast<String>().
                                       Order(StringComparer.Ordinal).
                                       ToList();

            Assert.That(resources.Keys.Order(StringComparer.Ordinal), Is.EqualTo(upstream),
                        "The embedded OpenAPI files differ from the upstream files as a set: " +
                        $"missing in WWCP S2: [{String.Join(", ", upstream.Except(resources.Keys))}], " +
                        $"not upstream any more: [{String.Join(", ", resources.Keys.Except(upstream))}]");

            var differences = new List<String>();

            foreach (var fileName in upstream)
            {

                var upstreamLines  = NormalizedLines(File.ReadAllText(Path.Combine(UpstreamOpenAPI, fileName)));
                var embeddedLines  = NormalizedLines(EmbeddedSchemas.Read(resources[fileName]));

                if (upstreamLines.SequenceEqual(embeddedLines, StringComparer.Ordinal))
                    continue;

                var firstDifference = Enumerable.Range(0, Math.Max(upstreamLines.Length, embeddedLines.Length)).
                                                 First(i => i >= upstreamLines.Length || i >= embeddedLines.Length || upstreamLines[i] != embeddedLines[i]);

                differences.Add($"{fileName} (first difference at line {firstDifference + 1}: " +
                                $"upstream '{(firstDifference < upstreamLines.Length ? upstreamLines[firstDifference] : "<end of file>")}', " +
                                $"embedded '{(firstDifference < embeddedLines.Length ? embeddedLines[firstDifference] : "<end of file>")}')");

            }

            Assert.That(differences, Is.Empty,
                        "The embedded copy of these OpenAPI files differs from the pinned upstream files (libs/s2-connect): " +
                        String.Join("; ", differences) + ". Either the pin moved, or WWCP S2 needs to refresh its copy.");

        }

        #endregion

        #region ThirdPartyNotices_NameThePinnedCommit()

        [Test]
        [S2C("Schemas.S2Connect.Provenance")]
        public async Task ThirdPartyNotices_NameThePinnedCommit()
        {
            RequireUpstream();
            await Provenance.AssertNoticeNamesThePinnedCommitAsync("S2 Connect (flexiblepower/s2-connect)", RepositoryPaths.S2Connect);
        }

        #endregion

    }

}
