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

using System.Reflection;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using Tomlyn;
using Tomlyn.Model;

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Specification
{

    /// <summary>
    /// The structured documentation of the S2 data model (flexiblepower/s2-documentation,
    /// structured-documentation/*.toml: one file per message, object or enumeration, the
    /// single source of truth the documentation website and s2-rust are generated from),
    /// pinned as the submodule libs/s2-documentation, held against the S2 JSON v1.0.0
    /// schemas WWCP S2 embeds and against WWCP S2's own types: every documented message has a
    /// schema and a sample message, documented fields are the schema's properties, and
    /// every documented enumeration value is accepted by the WWCP S2 type of the same name.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Specification)]
    public class S2DocumentationTests
    {

        #region Data

        private static String StructuredDocumentation
            => Path.Combine(RepositoryPaths.S2Documentation, "structured-documentation");

        /// <summary>
        /// One documented type: a message (sent_by present), an object (fields) or an
        /// enumeration (variants).
        /// </summary>
        private sealed record DocumentedType(String                       Name,
                                             Boolean                      IsMessage,
                                             IReadOnlyList<String>?       Variants,
                                             IReadOnlyDictionary<String, Boolean>?  Fields)  // field name => optional
        {
            public Boolean IsEnumeration  => Variants is not null;
        }

        #endregion


        #region (private) Helpers

        private static void RequireUpstream()
        {
            if (!Directory.Exists(StructuredDocumentation))
                Assert.Fail($"The s2-documentation submodule is not checked out at '{RepositoryPaths.S2Documentation}': run 'git submodule update --init libs/s2-documentation'.");
        }

        private static IReadOnlyList<DocumentedType> LoadDocumentation()
        {

            var types = new List<DocumentedType>();

            foreach (var file in Directory.GetFiles(StructuredDocumentation, "*.toml").Order(StringComparer.Ordinal))
            {

                var model  = Toml.ToModel(File.ReadAllText(file), file);
                var name   = model["type_name"] as String ?? Path.GetFileNameWithoutExtension(file);

                if (model.TryGetValue("variants", out var variantsValue) && variantsValue is TomlTable variants)
                {
                    types.Add(new DocumentedType(name, false, variants.Keys.Order(StringComparer.Ordinal).ToList(), null));
                    continue;
                }

                var fields = new Dictionary<String, Boolean>(StringComparer.Ordinal);

                if (model.TryGetValue("fields", out var fieldsValue) && fieldsValue is TomlTable fieldTable)
                    foreach (var (fieldName, fieldValue) in fieldTable)
                        fields[fieldName] = fieldValue is TomlTable field && field.TryGetValue("optional", out var optional) && optional is true;

                types.Add(new DocumentedType(name, model.ContainsKey("sent_by"), null, fields));

            }

            return types;

        }

        private static JObject? SchemaOf(String TypeName)
        {

            var messages  = Path.Combine(RepositoryPaths.S2Json, "messages", TypeName + ".schema.json");
            var schemas   = Path.Combine(RepositoryPaths.S2Json, "schemas",  TypeName + ".schema.json");

            var file = File.Exists(messages) ? messages : File.Exists(schemas) ? schemas : null;

            return file is null
                       ? null
                       : JObject.Parse(File.ReadAllText(file));

        }

        /// <summary>
        /// The WWCP S2 type modelling the documented enumeration: "PEBC.PowerEnvelopeLimitType"
        /// is PEBC_PowerEnvelopeLimitType, the plural documentation names are singular in
        /// WWCP S2 (ReceptionStatusValues => ReceptionStatusValue, RevokableObjects => RevokableObject).
        /// </summary>
        private static Type? WWCPEnumerationType(String DocumentedName)
        {

            var candidates = new List<String> { DocumentedName.Replace('.', '_') };

            if (DocumentedName.EndsWith('s'))
                candidates.Add(DocumentedName[..^1].Replace('.', '_'));

            var assembly = typeof(ControlType).Assembly;

            return candidates.Select(candidate => assembly.GetType(typeof(ControlType).Namespace + "." + candidate)).
                              FirstOrDefault(type => type is not null);

        }

        private static Boolean WWCPAccepts(Type EnumerationType, String Value)
        {

            var tryParse = EnumerationType.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, [ typeof(String) ])
                               ?? throw new InvalidOperationException($"{EnumerationType.Name} has no 'static TryParse(String)' method!");

            return tryParse.Invoke(null, [ Value ]) is not null;

        }

        #endregion


        #region EveryDocumentedMessage_HasASchemaAndASample()

        /// <summary>
        /// The documented messages, the message schemas and the sample messages of this suite
        /// are the same set of message types.
        /// </summary>
        [Test]
        [S2C("Documentation.Messages.Coverage")]
        public void EveryDocumentedMessage_HasASchemaAndASample()
        {

            RequireUpstream();

            var documented  = LoadDocumentation().Where(type => type.IsMessage).Select(type => type.Name).Order(StringComparer.Ordinal).ToList();
            var schemas     = Directory.GetFiles(Path.Combine(RepositoryPaths.S2Json, "messages"), "*.schema.json").
                                        Select(file => Path.GetFileName(file)[..^".schema.json".Length]).
                                        Order(StringComparer.Ordinal).
                                        ToList();
            var samples     = SampleMessages.All().Select(message => message.MessageType).Distinct().Order(StringComparer.Ordinal).ToList();

            var undocumented  = schemas.Except(documented).ToList();
            var notMessages   = documented.Except(schemas).ToList();

            Assert.That(samples, Is.EqualTo(schemas), "the sample messages cover exactly the message schemas");

            // D1: DDBC.PresentDemandStatus is a v1.0.0 message without documentation; the
            // documentation still describes the older DDBC shape (see also P3 and R6).
            Interop.KnownIssue("The structured documentation has no entry for the v1.0.0 message DDBC.PresentDemandStatus",
                               undocumented.SequenceEqual([ "DDBC.PresentDemandStatus" ]),
                               $"message schemas without documentation: {String.Join(", ", undocumented)}");

            // D4: DDBC.ActuatorDescription carries a sent_by entry although it is an object
            // inside DDBC.SystemDescription, not a message.
            Interop.KnownIssue("The structured documentation marks DDBC.ActuatorDescription as a message (sent_by) although it is an object type",
                               notMessages.SequenceEqual([ "DDBC.ActuatorDescription" ]),
                               $"documented as messages without being one: {String.Join(", ", notMessages)}");

        }

        #endregion

        #region DocumentedFields_AreTheSchemaProperties()

        /// <summary>
        /// For every documented message and object, the documented fields are exactly the
        /// properties of its schema and the non-optional fields are exactly the required ones;
        /// for every documented enumeration, the variants are exactly the schema's enum values.
        /// </summary>
        [Test]
        [S2C("Documentation.DataModel.Consistency")]
        public void DocumentedFields_AreTheSchemaProperties()
        {

            RequireUpstream();

            var differences = new List<String>();

            foreach (var type in LoadDocumentation())
            {

                var schema = SchemaOf(type.Name);

                if (schema is null)
                {
                    differences.Add($"{type.Name}: no schema");
                    continue;
                }

                if (type.IsEnumeration)
                {

                    var schemaValues = (schema["enum"] as JArray)?.Select(value => value.Value<String>()!).Order(StringComparer.Ordinal).ToList() ?? [];

                    if (!schemaValues.SequenceEqual(type.Variants!))
                        differences.Add($"{type.Name}: documented only [{String.Join(", ", type.Variants!.Except(schemaValues))}], schema only [{String.Join(", ", schemaValues.Except(type.Variants!))}]");

                    continue;

                }

                if (type.Fields is null || type.Fields.Count == 0)
                    continue;   // Duration, ID: simple types documented without fields

                var properties  = (schema["properties"] as JObject)?.Properties().Select(property => property.Name).Order(StringComparer.Ordinal).ToList() ?? [];
                var required    = (schema["required"]   as JArray)?.Select(value => value.Value<String>()!).Order(StringComparer.Ordinal).ToList() ?? [];
                var documented  = type.Fields.Keys.Order(StringComparer.Ordinal).ToList();
                var mandatory   = type.Fields.Where(field => !field.Value).Select(field => field.Key).Order(StringComparer.Ordinal).ToList();

                if (!properties.SequenceEqual(documented))
                    differences.Add($"{type.Name}: documented fields only [{String.Join(", ", documented.Except(properties))}], schema properties only [{String.Join(", ", properties.Except(documented))}]");
                else if (!required.SequenceEqual(mandatory))
                    differences.Add($"{type.Name}: documented as mandatory but optional in the schema [{String.Join(", ", mandatory.Except(required))}], required in the schema but documented as optional [{String.Join(", ", required.Except(mandatory))}]");

            }

            // D2 and D3: the documentation describes DDBC.SystemDescription with the older
            // present_demand_rate field (the shape s2-python and s2-rust implement, P2/R6) and
            // lists four revokable objects the v1.0.0 RevokableObjects enumeration does not have.
            var known = new[] {
                "DDBC.SystemDescription: documented fields only [present_demand_rate], schema properties only []",
                "RevokableObjects: documented only [DDBC.AverageDemandRateForecast, FRBC.FillLevelTargetProfile, FRBC.LeakageBehaviour, FRBC.UsageForecast], schema only []"
            };

            var unexpected = differences.Except(known).ToList();

            Assert.That(unexpected, Is.Empty, "the structured documentation and the v1.0.0 schemas disagree beyond the known issues: " + String.Join("; ", unexpected));

            Interop.KnownIssue("The structured documentation describes DDBC.SystemDescription with a present_demand_rate field and RevokableObjects with four values that S2 JSON v1.0.0 does not have",
                               differences.Order(StringComparer.Ordinal).SequenceEqual(known.Order(StringComparer.Ordinal)),
                               String.Join("; ", differences));

        }

        #endregion

        #region EveryDocumentedEnumerationValue_IsAcceptedByWWCP()

        /// <summary>
        /// Every documented enumeration has a WWCP S2 type of the same name, and every
        /// documented value that the v1.0.0 schema has as well is accepted by that type.
        /// (Documented values missing from the schema are covered above.)
        /// </summary>
        [Test]
        [S2C("Documentation.Enumerations.WWCP")]
        public void EveryDocumentedEnumerationValue_IsAcceptedByWWCP()
        {

            RequireUpstream();

            var enumerations  = LoadDocumentation().Where(type => type.IsEnumeration).ToList();
            var problems      = new List<String>();

            Assert.That(enumerations, Is.Not.Empty);

            foreach (var enumeration in enumerations)
            {

                var wwcpType = WWCPEnumerationType(enumeration.Name);

                if (wwcpType is null)
                {
                    problems.Add($"{enumeration.Name}: no WWCP S2 type");
                    continue;
                }

                var schemaValues  = (SchemaOf(enumeration.Name)?["enum"] as JArray)?.Select(value => value.Value<String>()!).ToHashSet(StringComparer.Ordinal) ?? [];
                var rejected      = enumeration.Variants!.Where(schemaValues.Contains).Where(value => !WWCPAccepts(wwcpType, value)).ToList();

                if (rejected.Count > 0)
                    problems.Add($"{enumeration.Name}: {wwcpType.Name} rejects [{String.Join(", ", rejected)}]");

            }

            Assert.That(problems, Is.Empty, String.Join("; ", problems));

        }

        #endregion

    }

}
