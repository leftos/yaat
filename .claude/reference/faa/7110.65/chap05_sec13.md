# 7110.65 - Chapter 5: Radar

## Section 13. Automation En Route

## Section 13. Automation- En Route

#### 5-13-1. CONFLICT ALERT (CA) AND MODE C INTRUDER (MCI) ALERT

1. When a CA or MCI alert is displayed, evaluate the reason for the alert without delay and take appropriate action.

   REFERENCE-

   FAA Order JO 7110.65, Para [2-1-6](./chap2_section_1.html#aZI3c0JACK), Safety Alert.
2. If another controller is involved in the alert, initiate coordination to ensure an effective course of action. Coordination is not required when immediate action is dictated.
3. Suppressing/Inhibiting CA/MCI alert.
   1. The controller may suppress the display of a CA/MCI alert from a control position with the application of one of the following suppress/inhibit computer functions:
      1. The Conflict Suppress (CO) function may be used to suppress the CA/MCI display between specific aircraft for a specific alert.

         NOTE-

         See NAS-MD-678 for the EARTS conflict suppress message.
      2. The Group Suppression (SG) function must be applied exclusively to inhibit the displaying of alerts among military aircraft engaged in special military operations where standard en route separation criteria does not apply.

         NOTE-

         Special military operations where the SG function would typically apply involve those activities where military aircraft routinely operate in proximities to each other that are less than standard en route separation criteria; i.e., air refueling operations, ADC practice intercept operations, etc.
   2. The computer entry of a message suppressing a CA/MCI alert constitutes acknowledgment for the alert and signifies that appropriate action has or will be taken.
   3. The CA/MCI alert may not be suppressed or inhibited at or for another control position without being coordinated.

#### 5-13-2. EN ROUTE MINIMUM SAFE ALTITUDE WARNING (E‐MSAW)

1. When an E‐MSAW alert is displayed, immediately analyze the situation and take the appropriate action to resolve the alert.

   NOTE-

   Caution should be exercised when issuing a clearance to an aircraft in reaction to an E‐MSAW alert to ensure that adjacent MIA areas are not a factor.


   REFERENCE-

   FAA Order JO 7110.65, Para [2-1-6](./chap2_section_1.html#aZI3c0JACK), Safety Alert.
2. The controller may suppress the display of an E‐MSAW alert from his/her control position with the application of one of the following suppress/inhibit computer functions:
   1. The specific alert suppression message may be used to inhibit the E‐MSAW alerting display on a single flight for a specific alert.
   2. The indefinite alert suppression message must be used exclusively to inhibit the display of E‐MSAW alerts on aircraft known to be flying at an altitude that will activate the alert feature of one or more MIA areas within an ARTCC.

      NOTE-

      1. The indefinite alert suppression message will remain in effect for the duration of the referenced flight's active status within the ARTCC unless modified by controller action.
      2. The indefinite alert suppression message would typically apply to military flights with clearance to fly low‐level type routes that routinely require altitudes below established minimum IFR altitudes.
3. The computer entry of a message suppressing or inhibiting E‐MSAW alerts constitutes acknowledgment for the alert and indicates that appropriate action has or will be taken to resolve the situation.

#### 5-13-3. COMPUTER ENTRY OF FLIGHT PLAN INFORMATION

1. Altitude
   1. The altitude field(s) of the data block must always reflect the current status of the aircraft unless otherwise specified in an appropriate facility directive.
   2. Unless otherwise specified in a facility directive or letter of agreement, do not modify assigned or interim altitude information prior to establishing communication with an aircraft that is outside your area of jurisdiction unless verbal coordination identifying who will modify the data block has been accomplished.

      NOTE-

      1. A local interim altitude (LIA) can be used as a means of recording interfacility coordination.
      2. Conflict probe in EDST does not probe for the LIA.
   3. Whenever an aircraft is cleared to maintain an altitude different from that in the flight plan database, enter into the computer one of the following:
      1. The new assigned altitude if the aircraft will (climb or descend to and) maintain the new altitude, or
      2. An interim altitude if the aircraft will (climb or descend to and) maintain the new altitude for a short period of time and subsequently be recleared to the altitude in the flight plan database or a new altitude or a new interim altitude, or

         *ERAM*
      3. A procedure altitude if the aircraft is cleared to vertically navigate (VNAV) on a SID/STAR with published restrictions, or
      4. Where appropriate for interfacility handoffs, an LIA when the assigned altitude differs from the coordinated altitude unless verbally coordinated or specified in a letter of agreement or facility directive.

         NOTE-

         A facility directive may be published, in accordance with JO 7210.3, paragraph 8-2-7, Waiver to Interim Altitude Requirements, deleting the interim altitude computer entry requirements of subparagraph 3.
2. Flight Plan Route Data

   This information must not be modified outside of the controller's area of jurisdiction unless verbally coordinated or specified in a Letter of Agreement or Facility Directive.

#### 5-13-4. ENTRY OF REPORTED ALTITUDE

Whenever Mode C altitude information is either not available or is unreliable, enter reported altitudes into the computer as follows:

NOTE-

Altitude updates are required to assure maximum accuracy in applying slant range correction formulas.

1. When an aircraft reaches the assigned altitude.
2. When an aircraft at an assigned altitude is issued a clearance to climb or descend.
3. A minimum of each 10,000 feet during climb to or descent from FL 180 and above.

#### 5-13-5. SELECTED ALTITUDE LIMITS

The display of Mode C targets and limited data blocks is necessary for application of Merging Target Procedures. Sectors must ensure the display of Mode C targets and data blocks by entering appropriate altitude limits and display filters to include, as a minimum, the altitude stratum of the sector plus:

1. 1,200 feet above the highest and below the lowest altitude or flight level of the sector where 1,000 feet vertical separation is applicable; and
2. 2,200 feet above the highest and below the lowest flight level of the sector where 2,000 feet vertical separation is applicable.

   NOTE-

   1. The data block, for purposes of this paragraph, must contain the Mode C altitude and call sign or beacon codeat a minimum.
   2. Exception to these requirements may be authorized for specific altitudes in certain ARTCC sectors if defined in appropriate facility directives and approved by the respective service area operations directorate.

#### 5-13-6. SECTOR ELIGIBILITY

The use of the OK function is allowed to override sector eligibility only when one of the following conditions is met:

1. Prior coordination is effected.
2. The flight is within the control jurisdiction of the sector.

#### 5-13-7. COAST TRACKS

Do not use coast tracks in the application of either radar or nonradar separation criteria.

#### 5-13-8. CONTROLLER INITIATED COAST TRACKS

1. Initiate coast tracks only in Flight Plan Aided Tracking (FLAT) mode, except “free” coast tracking may be used as a reminder that aircraft without corresponding computer‐stored flight plan information are under your control.

   NOTE-

   To ensure tracks are started in FLAT mode, perform a start track function at the aircraft's most current reported position, then immediately “force” the track into coast tracking by performing another start function with “CT” option in field 64. Making amendments to the stored route with trackball entry when the aircraft is rerouted, and repositioning the data block to coincide with the aircraft's position reports are methods of maintaining a coast track in FLAT mode.
2. Prior to initiating a coast track, ensure that a departure message or progress report corresponding with the aircraft's current position is entered into the computer.
3. As soon as practicable after the aircraft is in radar surveillance, initiate action to cause radar tracking to begin on the aircraft.

#### 5-13-9. ERAM COMPUTER ENTRY OF HOLD INFORMATION

1. When an aircraft is issued holding instructions, the delay is ATC initiated, and the EFC is other than “no delay expected:"
   1. Enter a hold message.
   2. Maintain a paired track.
   3. Enter an EFC time via a hold message, the Hold Data Menu, or the Hold View.
   4. Enter non-published holding instructions via a hold message or the Hold Data Menu.

      NOTE-

      The ERAM hold message allows automatic calculation and reporting of aggregate delays.
2. Unless otherwise specified in a facility directive, verbally coordinate non-published holding instructions when handing off an aircraft in hold status to another ERAM sector.
3. An EFC time entered into the Hold Data Menu, Hold View, or the hold message constitutes coordination of the EFC between ERAM sectors.

   REFERENCE-

   FAA Order JO 7210.3, Para 8-2-9, ERAM Hold Information Facility Directive Requirements.

#### 5-13-10. ERAM VISUAL INDICATOR OF SPECIAL ACTIVITY AIRSPACE (SAA) STATUS

Sector controllers must ensure the situation display's Airspace Status View/SAA Filter View accurately reflects the status of adapted Special Activity Airspace (SAA) that impact their area of control responsibility.When “SAA DOWN” is displayed in the Outage View, manually create visual indicators on the situation display to reflect changes to airspace status.

NOTE-

The “SAA DOWN” message in the Outage View means that SAA status is no longer being updated. The status of each adapted SAA at the time of the failure, whether “on” or “off”, will continue to be displayed. Status changes will not be automatically updated in the display until the outage is resolved.
