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

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// One well-formed instance of every S2 JSON v1.0.0 message (35 with a message_id plus
    /// ReceptionStatus), built the way the reference implementations expect them: every
    /// identifier is a UUID (s2-python and s2-rust reject anything else), every timestamp is
    /// timezone-aware with whole microseconds (so that a round trip compares equal), and every
    /// semantic rule of the schema descriptions holds. The values follow the EV charger and PV
    /// examples of the S2 documentation where they exist.
    /// </summary>
    public static class SampleMessages
    {

        #region Fixed values

        /// <summary>
        /// The reference time of every sample.
        /// </summary>
        public static readonly DateTimeOffset  T0  = new (2026, 9, 5, 12, 0, 0, 123, TimeSpan.Zero);

        private static readonly Message_Id       messageId          = Message_Id.      Parse("0f1e2d3c-4b5a-4697-8877-665544332211");
        private static readonly Resource_Id      resourceId         = Resource_Id.     Parse("11111111-1111-4111-8111-111111111101");
        private static readonly Actuator_Id      actuatorId         = Actuator_Id.     Parse("11111111-1111-4111-8111-111111111102");
        private static readonly OperationMode_Id offId              = OperationMode_Id.Parse("11111111-1111-4111-8111-111111111103");
        private static readonly OperationMode_Id chargingId         = OperationMode_Id.Parse("11111111-1111-4111-8111-111111111104");
        private static readonly Transition_Id    transition1Id      = Transition_Id.   Parse("11111111-1111-4111-8111-111111111105");
        private static readonly Transition_Id    transition2Id      = Transition_Id.   Parse("11111111-1111-4111-8111-111111111106");
        private static readonly Timer_Id         timerId            = Timer_Id.        Parse("11111111-1111-4111-8111-111111111107");
        private static readonly Instruction_Id   instructionId      = Instruction_Id.  Parse("11111111-1111-4111-8111-111111111108");
        private static readonly PowerConstraints_Id        powerConstraintsId   = PowerConstraints_Id.       Parse("11111111-1111-4111-8111-111111111109");
        private static readonly PowerEnvelope_Id           powerEnvelopeId      = PowerEnvelope_Id.          Parse("11111111-1111-4111-8111-11111111110a");
        private static readonly EnergyConstraint_Id        energyConstraintId   = EnergyConstraint_Id.       Parse("11111111-1111-4111-8111-11111111110b");
        private static readonly PowerProfileDefinition_Id  powerProfileId       = PowerProfileDefinition_Id. Parse("11111111-1111-4111-8111-11111111110c");
        private static readonly PowerSequenceContainer_Id  containerId          = PowerSequenceContainer_Id. Parse("11111111-1111-4111-8111-11111111110d");
        private static readonly PowerSequence_Id           sequence1Id          = PowerSequence_Id.          Parse("11111111-1111-4111-8111-11111111110e");
        private static readonly PowerSequence_Id           sequence2Id          = PowerSequence_Id.          Parse("11111111-1111-4111-8111-11111111110f");

        #endregion


        #region Shared elements

        private static readonly PowerRange  offPower       = new (0,    0,     CommodityQuantity.ElectricPower3PhaseSymmetric);
        private static readonly PowerRange  chargingPower  = new (1400, 11000, CommodityQuantity.ElectricPower3PhaseSymmetric);

        private static IReadOnlyList<Transition> Transitions()
            => [ new Transition(transition1Id, offId,      chargingId, [ timerId ], [],          false, TransitionCosts: 0.5, TransitionDuration: Duration.FromMilliseconds(3000)),
                 new Transition(transition2Id, chargingId, offId,      [],          [ timerId ], false, TransitionDuration: Duration.FromMilliseconds(3000)) ];

        private static IReadOnlyList<Timer> Timers()
            => [ new Timer(timerId, Duration.FromMilliseconds(60000), "cool down") ];

        private static IReadOnlyList<PowerForecastValue> ForecastValues(Double Expected)
            => [ new PowerForecastValue(Expected, CommodityQuantity.ElectricPower3PhaseSymmetric,
                                        ValueUpperLimit: Expected + 500, ValueUpper95PPR: Expected + 300, ValueUpper68PPR: Expected + 100,
                                        ValueLower68PPR: Expected - 100, ValueLower95PPR: Expected - 300, ValueLowerLimit: Expected - 500) ];

        #endregion


        #region Common messages

        public static Handshake Handshake()
            => new (EnergyManagementRole.RM, [ Version.S2JSONVersion, Version.S2JSONLegacyVersion ], messageId);

        public static HandshakeResponse HandshakeResponse()
            => new (Version.S2JSONLegacyVersion, messageId);

        public static ReceptionStatus ReceptionStatus()
            => new (messageId, ReceptionStatusValue.OK, "Processed okay.");

        public static ResourceManagerDetails ResourceManagerDetails(Message_Id? MessageId = null)
            => new (resourceId,
                    [ new Role(RoleType.EnergyConsumer, Commodity.Electricity) ],
                    Duration.FromMilliseconds(3000),
                    [ ControlType.FillRateBasedControl, ControlType.NotControllable ],
                    ProvidesForecast:               false,
                    ProvidesPowerMeasurementTypes:  [ CommodityQuantity.ElectricPower3PhaseSymmetric ],
                    Name:                           "My Electric Vehicle RM",
                    Manufacturer:                   "ACME",
                    Model:                          "WallBox-b100",
                    SerialNumber:                   "123",
                    FirmwareVersion:                "v1.0",
                    Currency:                       Currency.EUR,
                    MessageId:                      MessageId ?? messageId);

        public static SelectControlType SelectControlType()
            => new (ControlType.FillRateBasedControl, messageId);

        public static SessionRequest SessionRequest()
            => new (SessionRequestType.Reconnect, "maintenance", messageId);

        public static PowerMeasurement PowerMeasurement()
            => new (T0, [ new PowerValue(CommodityQuantity.ElectricPower3PhaseSymmetric, 5500.5) ], messageId);

        public static PowerForecast PowerForecast()
            => new (T0,
                    [ new PowerForecastElement(Duration.FromMilliseconds(900000), ForecastValues(4000)),
                      new PowerForecastElement(Duration.FromMilliseconds(900000), ForecastValues(2000)) ],
                    messageId);

        public static InstructionStatusUpdate InstructionStatusUpdate()
            => new (instructionId, InstructionStatus.Started, T0, messageId);

        public static RevokeObject RevokeObject()
            => new (RevokableObject.FRBC_Instruction, S2Object_Id.Parse(instructionId.ToString()), messageId);

        #endregion

        #region PEBC

        public static PEBC_PowerConstraints PEBC_PowerConstraints()
            => new (powerConstraintsId,
                    T0,
                    PEBC_PowerEnvelopeConsequenceType.Vanish,
                    [ new PEBC_AllowedLimitRange(CommodityQuantity.ElectricPower3PhaseSymmetric, PEBC_PowerEnvelopeLimitType.UpperLimit, new NumberRange(0, 5000), false),
                      new PEBC_AllowedLimitRange(CommodityQuantity.ElectricPower3PhaseSymmetric, PEBC_PowerEnvelopeLimitType.LowerLimit, new NumberRange(-5000, 0), false) ],
                    ValidUntil:  T0.AddHours(1),
                    MessageId:   messageId);

        public static PEBC_EnergyConstraint PEBC_EnergyConstraint()
            => new (energyConstraintId, T0, T0.AddHours(1), 3000, -3000, CommodityQuantity.ElectricPower3PhaseSymmetric, messageId);

        public static PEBC_Instruction PEBC_Instruction()
            => new (instructionId,
                    T0,
                    false,
                    powerConstraintsId,
                    [ new PEBC_PowerEnvelope(powerEnvelopeId,
                                             CommodityQuantity.ElectricPower3PhaseSymmetric,
                                             [ new PEBC_PowerEnvelopeElement(Duration.FromMilliseconds(900000), 4000, -1000),
                                               new PEBC_PowerEnvelopeElement(Duration.FromMilliseconds(900000), 2000, -2000) ]) ],
                    messageId);

        #endregion

        #region PPBC

        public static PPBC_PowerProfileDefinition PPBC_PowerProfileDefinition()
            => new (powerProfileId,
                    T0,
                    T0.AddHours(4),
                    [ new PPBC_PowerSequenceContainer(containerId,
                          [ new PPBC_PowerSequence(sequence1Id,
                                                   [ new PPBC_PowerSequenceElement(Duration.FromMilliseconds(1800000), ForecastValues(2000)),
                                                     new PPBC_PowerSequenceElement(Duration.FromMilliseconds(1800000), ForecastValues(500)) ],
                                                   IsInterruptible:        true,
                                                   AbnormalConditionOnly:  false,
                                                   MaxPauseBefore:         Duration.FromMilliseconds(600000)),
                            new PPBC_PowerSequence(sequence2Id,
                                                   [ new PPBC_PowerSequenceElement(Duration.FromMilliseconds(3600000), ForecastValues(1200)) ],
                                                   IsInterruptible:        false,
                                                   AbnormalConditionOnly:  false) ]) ],
                    messageId);

        public static PPBC_PowerProfileStatus PPBC_PowerProfileStatus()
            => new ([ new PPBC_PowerSequenceContainerStatus(powerProfileId, containerId, PPBC_PowerSequenceStatus.Executing, sequence1Id, Duration.FromMilliseconds(120000)) ],
                    messageId);

        public static PPBC_ScheduleInstruction PPBC_ScheduleInstruction()
            => new (instructionId, powerProfileId, containerId, sequence1Id, T0, false, messageId);

        public static PPBC_StartInterruptionInstruction PPBC_StartInterruptionInstruction()
            => new (instructionId, powerProfileId, containerId, sequence1Id, T0, false, messageId);

        public static PPBC_EndInterruptionInstruction PPBC_EndInterruptionInstruction()
            => new (instructionId, powerProfileId, containerId, sequence1Id, T0, false, messageId);

        #endregion

        #region OMBC

        public static OMBC_SystemDescription OMBC_SystemDescription()
            => new (T0,
                    [ new OMBC_OperationMode(offId,      [ offPower ],      false, "off",      new NumberRange(0, 0)),
                      new OMBC_OperationMode(chargingId, [ chargingPower ], false, "charging", new NumberRange(0.1, 0.4)) ],
                    Transitions(),
                    Timers(),
                    messageId);

        public static OMBC_Status OMBC_Status()
            => new (chargingId, 0.75, offId, T0, messageId);

        public static OMBC_Instruction OMBC_Instruction()
            => new (instructionId, T0, chargingId, 0.5, false, messageId);

        public static OMBC_TimerStatus OMBC_TimerStatus()
            => new (timerId, T0, messageId);

        #endregion

        #region FRBC

        public static FRBC_SystemDescription FRBC_SystemDescription(DateTimeOffset?  ValidFrom   = null,
                                                                    Message_Id?      MessageId   = null)
        {

            var off       = new FRBC_OperationMode(offId,
                                                   [ new FRBC_OperationModeElement(new NumberRange(0, 100), new NumberRange(0, 0), [ offPower ]) ],
                                                   AbnormalConditionOnly:  false,
                                                   DiagnosticLabel:        "off");

            var charging  = new FRBC_OperationMode(chargingId,
                                                   [ new FRBC_OperationModeElement(new NumberRange(0, 100), new NumberRange(0.00065, 0.0051), [ chargingPower ], new NumberRange(0.1, 0.4)) ],
                                                   AbnormalConditionOnly:  false,
                                                   DiagnosticLabel:        "charging");

            var actuator  = new FRBC_ActuatorDescription(actuatorId,
                                                         [ Commodity.Electricity ],
                                                         [ off, charging ],
                                                         Transitions(),
                                                         Timers(),
                                                         DiagnosticLabel: "EV charger actuator");

            var storage   = new FRBC_StorageDescription(ProvidesLeakageBehaviour:        false,
                                                        ProvidesFillLevelTargetProfile:  true,
                                                        ProvidesUsageForecast:           false,
                                                        FillLevelRange:                  new NumberRange(0, 100),
                                                        DiagnosticLabel:                 "Battery SoC",
                                                        FillLevelLabel:                  "EV Battery SoC");

            return new FRBC_SystemDescription(ValidFrom ?? T0, [ actuator ], storage, MessageId ?? messageId);

        }

        public static FRBC_ActuatorStatus FRBC_ActuatorStatus()
            => new (actuatorId, chargingId, 1.0, offId, T0, messageId);

        public static FRBC_StorageStatus FRBC_StorageStatus()
            => new (42.5, messageId);

        public static FRBC_Instruction FRBC_Instruction()
            => new (instructionId, actuatorId, chargingId, 1.0, T0, false, messageId);

        public static FRBC_TimerStatus FRBC_TimerStatus()
            => new (timerId, actuatorId, T0, messageId);

        public static FRBC_FillLevelTargetProfile FRBC_FillLevelTargetProfile()
            => new (T0,
                    [ new FRBC_FillLevelTargetProfileElement(Duration.FromMilliseconds(3600000), new NumberRange(60, 80)),
                      new FRBC_FillLevelTargetProfileElement(Duration.FromMilliseconds(3600000), new NumberRange(80, 100)) ],
                    messageId);

        public static FRBC_LeakageBehaviour FRBC_LeakageBehaviour()
            => new (T0,
                    [ new FRBC_LeakageBehaviourElement(new NumberRange(0,  50),  0.001),
                      new FRBC_LeakageBehaviourElement(new NumberRange(50, 100), 0.002) ],
                    messageId);

        public static FRBC_UsageForecast FRBC_UsageForecast()
            => new (T0,
                    [ new FRBC_UsageForecastElement(Duration.FromMilliseconds(3600000), 0.01, 0.02, 0.015, 0.012, 0.008, 0.005, 0.0),
                      new FRBC_UsageForecastElement(Duration.FromMilliseconds(3600000), 0.02) ],
                    messageId);

        #endregion

        #region DDBC

        public static DDBC_SystemDescription DDBC_SystemDescription()
            => new (T0,
                    [ new DDBC_ActuatorDescription(actuatorId,
                                                   [ Commodity.Electricity ],
                                                   [ new DDBC_OperationMode(offId,      [ offPower ],      new NumberRange(0, 0),    false, "off"),
                                                     new DDBC_OperationMode(chargingId, [ chargingPower ], new NumberRange(0, 5000), false, "heating", new NumberRange(0.1, 0.4)) ],
                                                   Transitions(),
                                                   Timers(),
                                                   DiagnosticLabel: "heat pump actuator") ],
                    ProvidesAverageDemandRateForecast:  true,
                    MessageId:                          messageId);

        public static DDBC_ActuatorStatus DDBC_ActuatorStatus()
            => new (actuatorId, chargingId, 0.6, offId, T0, messageId);

        public static DDBC_Instruction DDBC_Instruction()
            => new (instructionId, T0, false, actuatorId, chargingId, 0.8, messageId);

        public static DDBC_TimerStatus DDBC_TimerStatus()
            => new (timerId, actuatorId, T0, messageId);

        public static DDBC_PresentDemandStatus DDBC_PresentDemandStatus()
            => new (new NumberRange(1000, 1500), messageId);

        public static DDBC_AverageDemandRateForecast DDBC_AverageDemandRateForecast()
            => new (T0,
                    [ new DDBC_AverageDemandRateForecastElement(Duration.FromMilliseconds(3600000), 1200, 1600, 1500, 1400, 1000, 900, 800),
                      new DDBC_AverageDemandRateForecastElement(Duration.FromMilliseconds(3600000), 800) ],
                    messageId);

        #endregion


        #region All() / TestCases

        /// <summary>
        /// Every sample message with its message type.
        /// </summary>
        public static IEnumerable<(String MessageType, IS2Message Message)> All()
        {

            yield return ("Handshake",                          Handshake());
            yield return ("HandshakeResponse",                  HandshakeResponse());
            yield return ("ReceptionStatus",                    ReceptionStatus());
            yield return ("ResourceManagerDetails",             ResourceManagerDetails());
            yield return ("SelectControlType",                  SelectControlType());
            yield return ("SessionRequest",                     SessionRequest());
            yield return ("PowerMeasurement",                   PowerMeasurement());
            yield return ("PowerForecast",                      PowerForecast());
            yield return ("InstructionStatusUpdate",            InstructionStatusUpdate());
            yield return ("RevokeObject",                       RevokeObject());

            yield return ("PEBC.PowerConstraints",              PEBC_PowerConstraints());
            yield return ("PEBC.EnergyConstraint",              PEBC_EnergyConstraint());
            yield return ("PEBC.Instruction",                   PEBC_Instruction());

            yield return ("PPBC.PowerProfileDefinition",        PPBC_PowerProfileDefinition());
            yield return ("PPBC.PowerProfileStatus",            PPBC_PowerProfileStatus());
            yield return ("PPBC.ScheduleInstruction",           PPBC_ScheduleInstruction());
            yield return ("PPBC.StartInterruptionInstruction",  PPBC_StartInterruptionInstruction());
            yield return ("PPBC.EndInterruptionInstruction",    PPBC_EndInterruptionInstruction());

            yield return ("OMBC.SystemDescription",             OMBC_SystemDescription());
            yield return ("OMBC.Status",                        OMBC_Status());
            yield return ("OMBC.Instruction",                   OMBC_Instruction());
            yield return ("OMBC.TimerStatus",                   OMBC_TimerStatus());

            yield return ("FRBC.SystemDescription",             FRBC_SystemDescription());
            yield return ("FRBC.ActuatorStatus",                FRBC_ActuatorStatus());
            yield return ("FRBC.StorageStatus",                 FRBC_StorageStatus());
            yield return ("FRBC.Instruction",                   FRBC_Instruction());
            yield return ("FRBC.TimerStatus",                   FRBC_TimerStatus());
            yield return ("FRBC.FillLevelTargetProfile",        FRBC_FillLevelTargetProfile());
            yield return ("FRBC.LeakageBehaviour",              FRBC_LeakageBehaviour());
            yield return ("FRBC.UsageForecast",                 FRBC_UsageForecast());

            yield return ("DDBC.SystemDescription",             DDBC_SystemDescription());
            yield return ("DDBC.ActuatorStatus",                DDBC_ActuatorStatus());
            yield return ("DDBC.Instruction",                   DDBC_Instruction());
            yield return ("DDBC.TimerStatus",                   DDBC_TimerStatus());
            yield return ("DDBC.PresentDemandStatus",           DDBC_PresentDemandStatus());
            yield return ("DDBC.AverageDemandRateForecast",     DDBC_AverageDemandRateForecast());

        }

        /// <summary>
        /// The sample messages as NUnit test cases, named by message type.
        /// </summary>
        public static IEnumerable<TestCaseData> TestCases()
            => All().Select(sample => new TestCaseData(sample.Message).SetArgDisplayNames(sample.MessageType));

        #endregion

    }

}
