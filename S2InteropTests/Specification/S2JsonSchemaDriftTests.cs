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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Specification
{

    /// <summary>
    /// WWCP S2 embeds a copy of the S2 JSON v1.0.0 schemas (flexiblepower/s2-json: 36 message
    /// and 41 type schemas) and validates every message against it. The normative files are
    /// pinned here as the submodule libs/s2-json. These tests compare the embedded copy with the
    /// pin, file by file and as a set, so that a schema change upstream (a new message, a
    /// renamed field, a tightened constraint) cannot go unnoticed: upstream-drift.yml moves
    /// the submodule to the upstream default branch every night and runs exactly this.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Specification)]
    public class S2JsonSchemaDriftTests
    {

        #region Data

        private const String MessagesMarker  = ".messages.";
        private const String SchemasMarker   = ".schemas.";

        private static String UpstreamMessages
            => Path.Combine(RepositoryPaths.S2Json, "messages");

        private static String UpstreamSchemas
            => Path.Combine(RepositoryPaths.S2Json, "schemas");

        #endregion


        #region (private) Helpers

        private static void RequireUpstream()
        {
            if (!Directory.Exists(UpstreamMessages) || !Directory.Exists(UpstreamSchemas))
                Assert.Fail($"The s2-json submodule is not checked out at '{RepositoryPaths.S2Json}': run 'git submodule update --init libs/s2-json'.");
        }

        private static IReadOnlyList<String> UpstreamFileNames(String Directory)
            => System.IO.Directory.GetFiles(Directory, "*.schema.json").
                                   Select(Path.GetFileName).
                                   Cast<String>().
                                   Order(StringComparer.Ordinal).
                                   ToList();

        private static String FileNameOf(String ResourceName, String Marker)
        {
            var index = ResourceName.IndexOf(Marker, StringComparison.Ordinal);
            return index < 0
                       ? ResourceName
                       : ResourceName[(index + Marker.Length)..];
        }

        /// <summary>
        /// Compare the upstream files of a directory with the embedded resources carrying the
        /// given marker: same set of file names, and per file the same JSON (JToken.DeepEquals,
        /// so that formatting is not drift, content is).
        /// </summary>
        private static void AssertSameSchemas(String                UpstreamDirectory,
                                              IEnumerable<String>   ResourceNames,
                                              String                Marker)
        {

            var resources  = ResourceNames.ToDictionary(name => FileNameOf(name, Marker), name => name, StringComparer.Ordinal);
            var upstream   = UpstreamFileNames(UpstreamDirectory);

            Assert.That(resources.Keys.Order(StringComparer.Ordinal), Is.EqualTo(upstream),
                        $"The embedded schema files differ from the upstream files in '{UpstreamDirectory}' as a set: " +
                        $"missing in WWCP S2: [{String.Join(", ", upstream.Except(resources.Keys))}], " +
                        $"not upstream any more: [{String.Join(", ", resources.Keys.Except(upstream))}]");

            var differences = new List<String>();

            foreach (var fileName in upstream)
            {

                var upstreamJSON  = JToken.Parse(File.ReadAllText(Path.Combine(UpstreamDirectory, fileName)));
                var embeddedJSON  = JToken.Parse(EmbeddedSchemas.Read(resources[fileName]));

                if (!JToken.DeepEquals(upstreamJSON, embeddedJSON))
                    differences.Add(fileName);

            }

            Assert.That(differences, Is.Empty,
                        "The embedded copy of these schema files differs from the pinned upstream files (libs/s2-json): " +
                        String.Join(", ", differences) + ". Either the pin moved, or WWCP S2 needs to refresh its copy.");

        }

        #endregion


        #region MessageSchemas_MatchTheUpstreamFiles()

        [Test]
        [S2C("Schemas.S2JSON.Messages.Drift")]
        public void MessageSchemas_MatchTheUpstreamFiles()
        {
            RequireUpstream();
            AssertSameSchemas(UpstreamMessages, EmbeddedSchemas.MessageSchemaNames, MessagesMarker);
        }

        #endregion

        #region TypeSchemas_MatchTheUpstreamFiles()

        [Test]
        [S2C("Schemas.S2JSON.Types.Drift")]
        public void TypeSchemas_MatchTheUpstreamFiles()
        {
            RequireUpstream();
            AssertSameSchemas(UpstreamSchemas, EmbeddedSchemas.TypeSchemaNames, SchemasMarker);
        }

        #endregion

        #region EverySampleMessage_HasAnUpstreamMessageSchema()

        /// <summary>
        /// The sample messages the interop tests send cover every message type; each of them
        /// must have a message schema upstream, and each upstream message schema must be
        /// covered by a sample. A new message type upstream therefore fails here first.
        /// </summary>
        [Test]
        [S2C("Schemas.S2JSON.Messages.Coverage")]
        public void EverySampleMessage_HasAnUpstreamMessageSchema()
        {

            RequireUpstream();

            var upstream  = UpstreamFileNames(UpstreamMessages).
                                Select(fileName => fileName[..^".schema.json".Length]).
                                Order(StringComparer.Ordinal).
                                ToList();

            var samples   = SampleMessages.All().
                                Select(message => message.MessageType).
                                Distinct().
                                Order(StringComparer.Ordinal).
                                ToList();

            Assert.That(samples, Is.EqualTo(upstream),
                        "The message types of the sample messages and the upstream message schemas differ: " +
                        $"no sample for [{String.Join(", ", upstream.Except(samples))}], " +
                        $"no upstream schema for [{String.Join(", ", samples.Except(upstream))}]");

        }

        #endregion

        #region ThirdPartyNotices_NameThePinnedCommit()

        /// <summary>
        /// WWCP S2 documents the upstream commit its copy was taken from in
        /// THIRD-PARTY-NOTICES.md; that commit must be the pinned submodule. A pin bump here
        /// without a refreshed copy and notice upstream, or the other way round, fails here.
        /// </summary>
        [Test]
        [S2C("Schemas.S2JSON.Provenance")]
        public async Task ThirdPartyNotices_NameThePinnedCommit()
        {
            RequireUpstream();
            await Provenance.AssertNoticeNamesThePinnedCommitAsync("S2 JSON (flexiblepower/s2-json)", RepositoryPaths.S2Json);
        }

        #endregion

    }

}
