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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// JSON parsing that keeps the wire text as it is: Newtonsoft's JObject.Parse turns every
    /// RFC 3339 string into a Date token, which would hide how a reference implementation
    /// really serialised a timestamp (and whether it carried a UTC offset at all).
    /// </summary>
    public static class JsonText
    {

        /// <summary>
        /// Parse a JSON object without converting date-like strings.
        /// </summary>
        /// <param name="Text">The JSON text.</param>
        public static JObject ParseObject(String Text)
        {

            using var reader = new JsonTextReader(new StringReader(Text)) {
                                   DateParseHandling   = DateParseHandling.None,
                                   FloatParseHandling  = FloatParseHandling.Double
                               };

            return JObject.Load(reader);

        }

    }

}
