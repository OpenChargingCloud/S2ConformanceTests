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

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Rust
{

    /// <summary>
    /// The S2 JSON message layer against the serde data model of s2-rust (s2energy-messaging,
    /// generated from its s2.schema.json): every WWCP_S2 message survives a parse/re-serialise
    /// round trip, and the rejections of malformed messages are compared.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Rust)]
    public sealed class RustMessageRoundTripTests
    {

        #region Setup / teardown

        private EchoDriver? echo;

        [OneTimeSetUp]
        public async Task StartDriver()
        {
            await Interop.RequireRustAsync();
            echo = await EchoDriver.StartRustAsync();
            TestContext.Progress.WriteLine($"[s2-rust] echo driver ready: {echo.Ready}");
        }

        [OneTimeTearDown]
        public async Task StopDriver()
        {
            if (echo is not null)
                await echo.DisposeAsync();
        }

        private EchoDriver Echo
            => echo ?? throw new InvalidOperationException("The echo driver is not running!");

        #endregion


        #region EveryMessage_SurvivesARoundTripThroughS2Rust(Message)

        /// <summary>
        /// Messages the s2-rust data model (generated from its own s2.schema.json) cannot process.
        /// </summary>
        private static readonly Dictionary<String, String> knownIssues = new (StringComparer.Ordinal) {

            ["DDBC.PresentDemandStatus"] = "the s2-rust Message enum has no DDBC.PresentDemandStatus variant (its s2energy-messaging/src/s2.schema.json " +
                                           "does not list the message), so an RM cannot report its present demand to an s2-rust CEM",

            ["DDBC.SystemDescription"]   = "the s2-rust DDBC.SystemDescription requires a field present_demand_rate that s2-json v1.0.0 does not define " +
                                           "(the demand rate is a separate DDBC.PresentDemandStatus message), so a v1.0.0 DDBC.SystemDescription is rejected"

        };

        [Test]
        [TestCaseSource(typeof(SampleMessages), nameof(SampleMessages.TestCases))]
        [S2C("Interop.s2-rust.Messages.RoundTrip")]
        public async Task EveryMessage_SurvivesARoundTripThroughS2Rust(IS2Message Message)
        {

            if (knownIssues.TryGetValue(Message.MessageType, out var issue))
            {
                var result = await Echo.RoundTripAsync(Message);
                Interop.KnownIssue(issue, !result.Success, result.Problem);
                return;
            }

            await Echo.AssertRoundTripAsync(Message);

        }

        #endregion


        #region Rejections

        [Test]
        [S2C("Interop.s2-rust.Messages.UUIDs")]
        public async Task NonUUIDIdentifier_IsRejectedByS2Rust_AndByWWCPInStrictMode()
        {
            var json = InvalidMessages.NonUUIDIdentifier();
            await Echo.AssertRejectedAsync(json, "uses a non-UUID identifier");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict, out _, out _), Is.False);
        }

        [Test]
        [S2C("Interop.s2-rust.Messages.Timestamps")]
        public async Task NaiveTimestamp_IsRejectedByS2Rust_AndByWWCPInStrictMode()
        {
            var json = InvalidMessages.NaiveTimestamp();
            await Echo.AssertRejectedAsync(json, "has a timestamp without a UTC offset");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict, out _, out _), Is.False);
        }

        [Test]
        public async Task UnknownMessageType_IsRejectedByBoth()
        {
            var json = InvalidMessages.UnknownMessageType();
            await Echo.AssertRejectedAsync(json, "has an unknown message_type");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        [Test]
        public async Task MissingMandatoryProperty_IsRejectedByBoth()
        {
            var json = InvalidMessages.MissingMandatoryProperty();
            await Echo.AssertRejectedAsync(json, "lacks the mandatory actuator_id");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        [Test]
        public async Task UnknownEnumerationValue_IsRejectedByBoth()
        {
            var json = InvalidMessages.UnknownEnumerationValue();
            await Echo.AssertRejectedAsync(json, "uses an unknown control type");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        [Test]
        public async Task AdditionalProperty_IsRejectedByS2Rust_AndByWWCPInStrictMode()
        {
            var json = InvalidMessages.AdditionalProperty();
            await Echo.AssertRejectedAsync(json, "carries an additional property");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict, out _, out _), Is.False);
        }

        [Test]
        public async Task OperationModeFactorAboveOne_IsRejectedByWWCP_ButAcceptedByS2Rust_KnownIssue()
        {

            var json = InvalidMessages.OperationModeFactorAboveOne();

            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False, "WWCP_S2 enforces operation_mode_factor ∈ [0, 1]");

            var (accepted, _) = await Echo.IsAcceptedAsync(json);

            Interop.KnownIssue("s2-rust does not enforce the range of operation_mode_factor (0 to 1) that the schema descriptions define (typify generates no numeric bounds)",
                               StillPresent:  accepted,
                               Detail:        "s2-rust accepted an FRBC.Instruction with operation_mode_factor 1.5");

        }

        [Test]
        public async Task InvertedRange_IsRejectedByWWCP_ButAcceptedByS2Rust_KnownIssue()
        {

            var json = InvalidMessages.InvertedRange();

            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False, "WWCP_S2 enforces start_of_range ≤ end_of_range");

            var (accepted, _) = await Echo.IsAcceptedAsync(json);

            Interop.KnownIssue("s2-rust does not enforce the semantic rule start_of_range ≤ end_of_range of the schema descriptions (s2-python does)",
                               StillPresent:  accepted,
                               Detail:        "s2-rust accepted an FRBC.LeakageBehaviour element with fill_level_range 60 to 40");

        }

        #endregion

    }

}
