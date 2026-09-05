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

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// Invalid variants of the sample messages, to compare what the reference
    /// implementations and WWCP_S2 reject.
    /// </summary>
    public static class InvalidMessages
    {

        /// <summary>
        /// An FRBC.Instruction whose actuator_id is not a UUID (allowed by the ID schema
        /// pattern, rejected by s2-python, s2-rust and WWCP_S2's strict parser).
        /// </summary>
        public static JObject NonUUIDIdentifier()
        {
            var json = SampleMessages.FRBC_Instruction().ToJSON();
            json["actuator_id"] = "actuator1";
            return json;
        }

        /// <summary>
        /// An FRBC.Instruction whose execution_time has no UTC offset.
        /// </summary>
        public static JObject NaiveTimestamp()
        {
            var json = SampleMessages.FRBC_Instruction().ToJSON();
            json["execution_time"] = "2026-09-05T12:00:00";
            return json;
        }

        /// <summary>
        /// A message of an unknown type.
        /// </summary>
        public static JObject UnknownMessageType()
            => new () {
                   ["message_type"]  = "FRBC.Bogus",
                   ["message_id"]    = "11111111-1111-4111-8111-1111111111ff"
               };

        /// <summary>
        /// An FRBC.Instruction without its mandatory actuator_id.
        /// </summary>
        public static JObject MissingMandatoryProperty()
        {
            var json = SampleMessages.FRBC_Instruction().ToJSON();
            json.Remove("actuator_id");
            return json;
        }

        /// <summary>
        /// An FRBC.Instruction with a property the schema does not define
        /// ("additionalProperties": false).
        /// </summary>
        public static JObject AdditionalProperty()
        {
            var json = SampleMessages.FRBC_Instruction().ToJSON();
            json["priority"] = 1;
            return json;
        }

        /// <summary>
        /// An FRBC.Instruction whose operation_mode_factor is above 1.
        /// </summary>
        public static JObject OperationModeFactorAboveOne()
        {
            var json = SampleMessages.FRBC_Instruction().ToJSON();
            json["operation_mode_factor"] = 1.5;
            return json;
        }

        /// <summary>
        /// A ResourceManagerDetails with an unknown control type (closed enumeration).
        /// </summary>
        public static JObject UnknownEnumerationValue()
        {
            var json = SampleMessages.ResourceManagerDetails().ToJSON();
            json["available_control_types"] = new JArray("MAGIC_CONTROL");
            return json;
        }

        /// <summary>
        /// A PowerRange whose start is above its end (a semantic rule of the schema description).
        /// </summary>
        public static JObject InvertedRange()
        {
            var json = SampleMessages.PowerMeasurement().ToJSON();
            // PowerMeasurement has no range; use an FRBC.LeakageBehaviour with an inverted fill level range instead.
            json = SampleMessages.FRBC_LeakageBehaviour().ToJSON();
            json["elements"]![0]!["fill_level_range"]!["start_of_range"] = 60;
            json["elements"]![0]!["fill_level_range"]!["end_of_range"]   = 40;
            return json;
        }

    }

}
