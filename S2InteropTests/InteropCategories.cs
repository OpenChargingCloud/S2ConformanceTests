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

namespace cloud.charging.open.protocols.S2.InteropTests
{

    /// <summary>
    /// The NUnit categories of the interoperability tests. Every fixture carries
    /// <see cref="Interop"/> (the nightly category of WWCP_S2, PLAN.md §8) and the
    /// name of the reference implementation it runs against.
    /// </summary>
    public static class InteropCategories
    {

        /// <summary>
        /// Interoperability tests against external reference implementations.
        /// </summary>
        public const String Interop  = "Interop";

        /// <summary>
        /// Tests against s2-python (libs/s2-python).
        /// </summary>
        public const String Python   = "s2-python";

        /// <summary>
        /// Tests against s2-rust (libs/s2-rust).
        /// </summary>
        public const String Rust     = "s2-rust";

        /// <summary>
        /// Tests against s2auth, the Python S2 Connect implementation (libs/s2auth).
        /// </summary>
        public const String S2Auth   = "s2auth";

        /// <summary>
        /// Tests holding WWCP S2 to the pinned specification material (libs/s2-json,
        /// libs/s2-connect, libs/s2-documentation); no external toolchain needed.
        /// </summary>
        public const String Specification = "Specification";

    }

}
