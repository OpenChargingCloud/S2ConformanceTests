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

using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// A JSON round-trip driver of a reference implementation (tools/s2-python-harness/s2_echo.py
    /// or the s2-json-echo binary of tools/s2-rust-harness): every request carries an id, the
    /// driver parses the message with the reference implementation's data model, re-serialises
    /// it and answers with the same id.
    /// </summary>
    public sealed class EchoDriver : IAsyncDisposable
    {

        #region Data

        private readonly ExternalProcess  process;
        private          Int32            nextId;

        #endregion

        #region Properties

        /// <summary>
        /// The name of the reference implementation, for messages.
        /// </summary>
        public String  Name    { get; }

        /// <summary>
        /// The "ready" event of the driver (versions).
        /// </summary>
        public JObject Ready   { get; }

        #endregion

        #region Constructor(s)

        private EchoDriver(String           Name,
                           ExternalProcess  Process,
                           JObject          Ready)
        {
            this.Name     = Name;
            this.process  = Process;
            this.Ready    = Ready;
        }

        #endregion


        #region (static) StartPythonAsync() / StartRustAsync()

        /// <summary>
        /// Start the s2-python echo driver.
        /// </summary>
        public static async Task<EchoDriver> StartPythonAsync()
        {
            var process  = await PythonHarness.StartAsync("s2_echo.py");
            var ready    = await process.WaitForEventAsync("ready", TimeSpan.FromSeconds(60));
            return new EchoDriver("s2-python", process, ready);
        }

        /// <summary>
        /// Start the s2-rust echo driver.
        /// </summary>
        public static async Task<EchoDriver> StartRustAsync()
        {
            var process  = await RustHarness.StartAsync("s2-json-echo");
            var ready    = await process.WaitForEventAsync("ready", TimeSpan.FromSeconds(60));
            return new EchoDriver("s2-rust", process, ready);
        }

        #endregion


        #region EchoAsync(Message)

        /// <summary>
        /// Send a message through the driver and return its answer
        /// ({"ok": true, "message": …} or {"ok": false, "error": …}).
        /// </summary>
        /// <param name="Message">The S2 JSON message.</param>
        public async Task<JObject> EchoAsync(JObject Message)
        {

            var id = Interlocked.Increment(ref nextId);

            await process.SendAsync(new JObject {
                                        ["id"]       = id,
                                        ["message"]  = Message
                                    });

            return await process.WaitForEventAsync(e => e["id"]?.Type == JTokenType.Integer && e.Value<Int32>("id") == id,
                                                   TimeSpan.FromSeconds(30),
                                                   $"the echo of request {id}");

        }

        #endregion

        #region RoundTripAsync(Message) / AssertRoundTripAsync(Message)

        /// <summary>
        /// The outcome of a round trip.
        /// </summary>
        /// <param name="Success">Whether the round trip preserved the message.</param>
        /// <param name="Problem">What went wrong otherwise.</param>
        public sealed record RoundTripResult(Boolean  Success,
                                             String?  Problem);

        /// <summary>
        /// Send the message through the reference implementation and check that it is
        /// accepted, that its re-serialisation is schema valid, is accepted by WWCP_S2's strict
        /// parser and equals the original message (no assertions, for known-issue checks).
        /// </summary>
        /// <param name="Message">A WWCP_S2 message.</param>
        public async Task<RoundTripResult> RoundTripAsync(IS2Message Message)
        {

            var json      = Message.ToJSON();
            var response  = await EchoAsync(json);

            if (!response.Value<Boolean>("ok"))
                return new RoundTripResult(false, $"{Name} rejected {Message.MessageType}: {response.Value<String>("error")}{Environment.NewLine}{json.ToString(Formatting.Indented)}");

            if (response["message"] is not JObject echoed)
                return new RoundTripResult(false, $"{Name} answered without a message object: {response}");

            var validation = S2SchemaValidator.ValidateMessage(echoed, Message.MessageType);

            if (!validation.IsValid)
            {
                var errors = validation.Details.
                                 Where (detail => detail.HasErrors).
                                 Select(detail => $"{detail.InstanceLocation}: {String.Join("; ", detail.Errors!.Select(error => error.Key + " " + error.Value))}");
                return new RoundTripResult(false, $"the {Name} re-serialisation of {Message.MessageType} violates its schema: {String.Join(" | ", errors)}{Environment.NewLine}{echoed.ToString(Formatting.Indented)}");
            }

            if (!S2MessageParser.TryParse(echoed, S2ParserOptions.Strict, out var message, out var error))
                return new RoundTripResult(false, $"WWCP_S2 (strict) could not parse the {Name} re-serialisation of {Message.MessageType}: {error}{Environment.NewLine}{echoed.ToString(Formatting.Indented)}");

            // Object equality is the right comparison: the wire text legitimately differs in
            // details such as the number of fractional digits of a timestamp ("…00.123Z" vs
            // "…00.123000Z") or the order of the properties.
            if (!message.Equals(Message))
                return new RoundTripResult(false, $"{Name} changed {Message.MessageType} on the way:{Environment.NewLine}sent:   {json.ToString(Formatting.None)}{Environment.NewLine}echoed: {echoed.ToString(Formatting.None)}");

            return new RoundTripResult(true, null);

        }

        /// <summary>
        /// Assert that the message survives the round trip (see <see cref="RoundTripAsync"/>).
        /// </summary>
        /// <param name="Message">A WWCP_S2 message.</param>
        public async Task AssertRoundTripAsync(IS2Message Message)
        {
            var result = await RoundTripAsync(Message);
            Assert.That(result.Success, Is.True, result.Problem);
        }

        #endregion

        #region IsAcceptedAsync(Message)

        /// <summary>
        /// Whether the reference implementation accepts the message (no assertions).
        /// </summary>
        /// <param name="Message">An S2 JSON message.</param>
        public async Task<(Boolean Accepted, String? Error)> IsAcceptedAsync(JObject Message)
        {
            var response = await EchoAsync(Message);
            return (response.Value<Boolean>("ok"), response.Value<String>("error"));
        }

        #endregion

        #region AssertRejectedAsync(Message, Why)

        /// <summary>
        /// Assert that the reference implementation rejects the message and return its error text.
        /// </summary>
        /// <param name="Message">An invalid S2 JSON message.</param>
        /// <param name="Why">Why it is invalid, for the failure message.</param>
        public async Task<String> AssertRejectedAsync(JObject  Message,
                                                      String   Why)
        {

            var response = await EchoAsync(Message);

            Assert.That(response.Value<Boolean>("ok"),
                        Is.False,
                        () => $"{Name} accepted a message that {Why}:{Environment.NewLine}{Message.ToString(Formatting.Indented)}");

            var error = response.Value<String>("error") ?? "";
            TestContext.Out.WriteLine($"{Name} rejected the message that {Why}: {error}");
            return error;

        }

        #endregion

        #region DisposeAsync()

        public ValueTask DisposeAsync()
            => process.DisposeAsync();

        #endregion

    }

}
