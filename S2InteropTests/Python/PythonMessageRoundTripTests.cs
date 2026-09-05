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

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Python
{

    /// <summary>
    /// The S2 JSON message layer against s2-python's pydantic data model: every WWCP_S2
    /// message survives a parse/re-serialise round trip through s2-python, and both
    /// implementations reject the same malformed messages.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Python)]
    public sealed class PythonMessageRoundTripTests
    {

        #region Setup / teardown

        private EchoDriver? echo;

        [OneTimeSetUp]
        public async Task StartDriver()
        {
            await Interop.RequirePythonAsync();
            echo = await EchoDriver.StartPythonAsync();
            TestContext.Progress.WriteLine($"[s2-python] echo driver ready: {echo.Ready}");
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


        #region EveryMessage_SurvivesARoundTripThroughS2Python(Message)

        /// <summary>
        /// Messages s2-python 0.10.0 cannot process, with the reason.
        /// </summary>
        private static readonly Dictionary<String, String> knownIssues = new (StringComparer.Ordinal) {

            ["PPBC.PowerProfileStatus"]  = "s2-python declares PPBCPowerSequenceContainerStatus.progress as uuid.UUID " +
                                           "(src/s2python/ppbc/ppbc_power_sequence_container_status.py) although the schema defines it as a Duration, " +
                                           "so every PPBC.PowerProfileStatus with a progress is rejected ('UUID input should be a string, bytes or UUID object')",

            ["DDBC.SystemDescription"]   = "the DDBC models of s2-python deviate from s2-json v1.0.0: DDBCOperationMode (src/s2python/ddbc/ddbc_operation_mode.py) " +
                                           "requires 'id' next to the schema's 'Id' and a list for supply_range instead of a NumberRange, and DDBCSystemDescription " +
                                           "requires a present_demand_rate field that v1.0.0 does not define (the demand rate is a separate DDBC.PresentDemandStatus " +
                                           "message), so a v1.0.0 DDBC.SystemDescription is rejected",

            ["DDBC.PresentDemandStatus"] = "s2-python has no DDBC.PresentDemandStatus message (S2Parser: 'Unable to parse DDBC.PresentDemandStatus as an S2 message. Type unknown.'), " +
                                           "so an RM cannot report its present demand to an s2-python CEM"

        };

        [Test]
        [TestCaseSource(typeof(SampleMessages), nameof(SampleMessages.TestCases))]
        [S2C("Interop.s2-python.Messages.RoundTrip")]
        public async Task EveryMessage_SurvivesARoundTripThroughS2Python(IS2Message Message)
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


        #region Rejections both implementations agree on

        [Test]
        [S2C("Interop.s2-python.Messages.UUIDs")]
        public async Task NonUUIDIdentifier_IsRejectedByS2Python_AndByWWCPInStrictMode()
        {

            var json = InvalidMessages.NonUUIDIdentifier();

            await Echo.AssertRejectedAsync(json, "uses a non-UUID identifier");

            Assert.Multiple(() => {
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict,  out _, out _), Is.False, "WWCP_S2 strict (RequireUUIDs) must reject the non-UUID identifier");
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.True,  "WWCP_S2 default accepts what the ID schema pattern allows");
            });

        }

        [Test]
        [S2C("Interop.s2-python.Messages.Timestamps")]
        public async Task NaiveTimestamp_IsRejectedByS2Python_AndByWWCPInStrictMode()
        {

            var json = InvalidMessages.NaiveTimestamp();

            await Echo.AssertRejectedAsync(json, "has a timestamp without a UTC offset");

            Assert.Multiple(() => {
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict,  out _, out _), Is.False, "WWCP_S2 strict (RejectNaiveTimestamps) must reject the naive timestamp");
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.True,  "WWCP_S2 default tolerates a naive timestamp");
            });

        }

        [Test]
        public async Task UnknownMessageType_IsRejectedByBoth()
        {
            var json = InvalidMessages.UnknownMessageType();
            await Echo.AssertRejectedAsync(json, "has an unknown message_type");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out var error), Is.False);
            Assert.That(error!.Status, Is.EqualTo(ReceptionStatusValue.InvalidMessage));
        }

        [Test]
        public async Task MissingMandatoryProperty_IsRejectedByBoth()
        {
            var json = InvalidMessages.MissingMandatoryProperty();
            await Echo.AssertRejectedAsync(json, "lacks the mandatory actuator_id");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        [Test]
        public async Task AdditionalProperty_IsRejectedByS2Python_AndByWWCPInStrictMode()
        {

            var json = InvalidMessages.AdditionalProperty();

            await Echo.AssertRejectedAsync(json, "carries an additional property");

            Assert.Multiple(() => {
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Strict,  out _, out _), Is.False, "WWCP_S2 strict (RejectAdditionalProperties) must reject the additional property");
                Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.True,  "WWCP_S2 default is liberal in what it accepts");
            });

        }

        [Test]
        public async Task OperationModeFactorAboveOne_IsRejectedByWWCP_ButAcceptedByS2Python_KnownIssue()
        {

            var json = InvalidMessages.OperationModeFactorAboveOne();

            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False, "WWCP_S2 enforces operation_mode_factor ∈ [0, 1]");

            var (accepted, _) = await Echo.IsAcceptedAsync(json);

            Interop.KnownIssue("s2-python does not enforce the range of operation_mode_factor (0 to 1) that the schema descriptions define",
                               StillPresent:  accepted,
                               Detail:        "s2-python accepted an FRBC.Instruction with operation_mode_factor 1.5");

        }

        [Test]
        public async Task UnknownEnumerationValue_IsRejectedByBoth()
        {
            var json = InvalidMessages.UnknownEnumerationValue();
            await Echo.AssertRejectedAsync(json, "uses an unknown control type");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        [Test]
        public async Task InvertedRange_IsRejectedByBoth()
        {
            var json = InvalidMessages.InvertedRange();
            await Echo.AssertRejectedAsync(json, "has a range whose start is above its end");
            Assert.That(S2MessageParser.TryParse(json, S2ParserOptions.Default, out _, out _), Is.False);
        }

        #endregion

    }

}
