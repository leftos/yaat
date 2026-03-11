# 7110.65 - Chapter 5: Radar

## Section 2. Beacon and ADS-B Systems

## Section 2. Beacon/ADS-B Systems

#### 5-2-1. ASSIGNMENT CRITERIA

1. General.
   1. Mode 3/A is designated as the common military/civil mode for air traffic control use.
   2. Make beacon code assignments to only ADS-B and/or transponder-equipped aircraft.

      NOTE-

      Aircraft equipped with ADS-B are also still required to have an operable transponder. The ATC-assigned beacon code is one of the required message elements of ADS-B Out.
2. Unless otherwise specified in this section, a facility directive, or a letter of agreement, issue beacon codes assigned by the computer. Computer-assigned codes may be modified as required.

   NOTE-

   The computer will assign only discrete beacon codes unless all the discrete codes allocated to a facility are in use.

   1. TERMINAL. Aircraft that will remain within the terminal facility's delegated airspace must be assigned a code from the code subset allocated to the terminal facility.
   2. TERMINAL. Unless otherwise specified in a facility directive or a letter of agreement, aircraft that will enter an adjacent facility's delegated airspace must be assigned a beacon code assigned by the ARTCC computer.

      NOTE-

      This will provide the adjacent facility advance information on the aircraft and will cause auto-acquisition of the aircraft prior to handoff. When an airborne aircraft that has been assigned a beacon code by the ARTCC computer and whose flight plan will terminate in another facility's area cancels ATC service, appropriate action should be taken to remove flight plan information on that aircraft.


      PHRASEOLOGY-

      SQUAWK THREE/ALFA (code),
       or
      SQUAWK (code).


      REFERENCE-

      FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.
      FAA Order JO 7110.65, Para [5-3-4](./chap5_section_3.html#p3Ib4JACK), Terminal Automation Systems Identification Methods.
3. Code 4000 should be assigned when aircraft are operating on a flight plan specifying frequent or rapid changes in assigned altitude in more than one stratum or other category of flight not compatible with a discrete code assignment.

   NOTE-

   1. Categories of flight that can be assigned Code 4000 include certain flight test aircraft, MTR missions, aerial refueling operation requiring descent involving more than one stratum, ALTRVs where continuous monitoring of ATC frequencies is not required and frequent altitude changes are approved, and other flights requiring special handling by ATC.
   2. Military aircraft operating in restricted/warning areas or on VR routes will squawk 4000 unless another code has been assigned or coordinated with ATC.

#### 5-2-2. RADAR BEACON CODE CHANGES

Unless otherwise specified in a directive or a letter of agreement or coordinated at the time of handoff, do not request an aircraft to change from the code it was squawking in the transferring facility's area until the aircraft is within your area of responsibility.

REFERENCE-

FAA Order JO 7110.65, Para [4-2-8](./chap4_section_2.html#1MuWt18catcn), IFR‐VFR and VFR‐IFR Flights.
FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-3. EMERGENCY CODE ASSIGNMENT

Assign codes to emergency aircraft as follows:

1. **Code 7700** when the pilot declares an emergency and the aircraft is not radar identified.

   PHRASEOLOGY-

   SQUAWK MAYDAY ON 7700.


   NOTE-

   Instead of displaying “7700" in the data block, ERAM will display “EMRG,” and STARS/MEARTS will display “EM.”
2. After radio and radar contact have been established, you may request other than single-piloted helicopters and single-piloted turbojet aircraft to change from **Code 7700** to a computer-assigned discrete code.

   NOTE-

   1. The code change, based on pilot concurrence, the nature of the emergency, and current flight conditions, will signify to other ATC facilities that the aircraft in distress is identified and under ATC control.
   2. Pilots of single-piloted helicopters and single-piloted turbojet aircraft may be unable to change transponder settings during an emergency.


   PHRASEOLOGY-

   RADAR CONTACT (position). IF FEASIBLE, SQUAWK (code).


   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.
3. The following must be accomplished on a Mode C equipped VFR aircraft which is in emergency but no longer requires the assignment of **Code 7700**:
   1. TERMINAL. Assign a beacon code that will permit terminal minimum safe altitude warning (MSAW) alarm processing.
   2. EN ROUTE. An appropriate keyboard entry must be made to ensure en route MSAW (EMSAW) alarm processing.

#### 5-2-4. RADIO FAILURE

When you observe a **Code 7600** display, apply the procedures in paragraph [10-4-4](./chap10_section_4.html#FLJ186JACK), Communications Failure.

NOTE-

1. *An aircraft experiencing a loss of two-way radio communications capability can be expected to squawk **Code 7600**.*
2. *Instead of displaying “7600” in the data block, ERAM will display “RDOF,” and STARS/MEARTS will display “RF.”*


REFERENCE-

FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-5. HIJACK/UNLAWFUL INTERFERENCE

When you observe a Code 7500 display, apply the procedures in paragraph [10-2-6](./chap10_section_2.html#V2K302JACK), Hijacked Aircraft.

NOTE-

Instead of displaying “7500” in the data block, ERAM will display “HIJK,” and STARS/MEARTS will display “HJ.”


REFERENCE-

FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-6. UNMANNED AIRCRAFT SYSTEMS (UAS) LOST LINK

**Code 7400** may be transmitted by unmanned aircraft systems (UAS) when the control link between the aircraft and the pilot is lost. Lost link procedures are programmed into the flight management system and associated with the flight plan being flown.

When you observe a **Code 7400** display, do the following:

NOTE-

Instead of displaying “7400” in the data block, ERAM will display “LLNK,” and STARS/MEARTS will display “LL.”

1. Determine the lost link procedure, as outlined in the Special Airworthiness Certificate or Certificate of Waiver or Authorization (COA).
2. Coordinate, as required, to allow UAS to execute the lost link procedure.
3. Advise the OS/CIC, when feasible, so the event can be documented.
4. If you observe or are informed by the PIC that the UAS is deviating from the programmed Lost Link procedure, or is encountering another anomaly, treat the situation in accordance with FAA Order JO 7110.65 [Chapter 10](./chap10_section_1.html#4B6r612e8Mary), [Section 1](./chap10_section_1.html#AZxqP12deTawa), paragraph [10-1-1](./chap10_section_1.html#pCJ244JACK)[c](./chap10_section_1.html#mYxqP1bTawa).

   NOTE-

   1. The available lost link procedure should, at a minimum, include lost link route of flight, lost link orbit points, lost link altitudes, communications procedures and preplanned flight termination points if the event recovery of the UAS is deemed unfeasible.
   2. Each lost link procedure may differ and is dependent upon airframe and operation. These items are contained in the flight's Certificate of Authorization or Waiver (COA) and must be made available to ATC personnel in their simplest form at positions responsible for Unmanned Aircraft (UAs).
   3. Some UA airframes (Global Hawk) will not be programmed upon the NAS Automation roll out to squawk
   4. These airframes will continue to squawk
   5. should a lost link occur. The ATC Specialist must apply the same procedures described above.

#### 5-2-7. VFR CODE ASSIGNMENTS

1. For VFR aircraft receiving radar advisories, issue a computer-assigned beacon code.
   1. If the aircraft is outside of your area of responsibility and an operational benefit will be gained by retaining the aircraft on your frequency for the purpose of providing services, ensure that coordination has been effected:
      1. As soon as possible after positive identification, and
      2. Prior to issuing a control instruction or providing a service other than a safety alert/traffic advisory.

         NOTE-

         Safety alerts/traffic advisories may be issued to an aircraft prior to coordination if an imminent situation may be averted by such action. Coordination should be effected as soon as possible thereafter.
2. Instruct an IFR aircraft that cancels its IFR flight plan and is not requesting radar advisory service, or a VFR aircraft for which radar advisory service is being terminated, to squawk VFR.

   PHRASEOLOGY-

   SQUAWK VFR.
    or
   SQUAWK 1200.


   NOTE-

   1. Aircraft not in contact with ATC may squawk
   2. in lieu of
   3. while en route to/from or within designated firefighting areas.
   4. VFR aircraft that fly authorized SAR missions for the USAF or USCG may be advised to squawk
   5. in lieu of
   6. while en route to/from or within the designated search area.
   7. VFR gliders should squawk
   8. in lieu of
   9. Gliders operate under some flight and maneuvering limitations. They may go from essentially stationary targets while climbing and thermaling to moving targets very quickly. They can be expected to make radical changes in flight direction to find lift and cannot hold altitude in a response to an ATC request. Gliders may congregate together for short periods of time to climb together in thermals and may cruise together in loose formations while traveling between thermals.
   10. The lead aircraft in a standard VFR formation flight not in contact with ATC should squawk 1203 in lieu of 1200. All other aircraft in the formation should squawk standby.


   REFERENCE-

   FAA Order JO 7110.66, National Beacon Code Allocation Plan.
3. When an aircraft changes from VFR to IFR, assign a beacon code to Mode C equipped aircraft that will allow MSAW alarms.

   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-8. BEACON CODES FOR PRESSURE SUIT FLIGHTS AND FLIGHTS ABOVE FL 600

Special use Mode 3/A codes are reserved for certain pressure suit flights and aircraft operations above FL 600 in accordance with FAA Order JO 7610.4, Sensitive Procedures and Requirements for Special Operations, Appendix 4, Document 2.

1. Ensure that these flights remain on one of the special use codes if filed in the flight plan, except:
2. When unforeseen events cause more than one aircraft to be in the same or adjacent ARTCC's airspace at the same time on the same special use discrete code, if necessary, you may request the pilot to make a code change, squawk standby, or stop squawk as appropriate.

   NOTE-

   1. Current FAA automation systems track multiple targets on the same beacon code with much greater reliability than their predecessors, and a code change may not be necessary for such flights.
   2. The beacon code is often preset on the ground for such flights and is used throughout the flight profile, including operations below FL 600. Due to equipment inaccessibility, the flight crew may not be able to accept transponder changes identified in this subparagraph.
   3. In case of emergency, Code 7700 can still be activated. Instead of displaying “7700” in the data block, ERAM will display “EMRG,” and STARS/MEARTS will display “EM.”


   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-9. AIR DEFENSE EXERCISE BEACON CODE ASSIGNMENT

EN ROUTE

Ensure exercise FAKER aircraft remain on the exercise flight plan filed discrete beacon code.

NOTE-

1. *NORAD will ensure exercise FAKER aircraft flight plans are filed containing discrete beacon codes from the Department of Defense code allocation specified in FAA Order JO 7610.4, Sensitive Procedures and Requirements for Special Operations, Appendix 6.*
2. *NORAD will ensure that those FAKER aircraft assigned the same discrete beacon code are not flight planned in the same or any adjacent ARTCC's airspace at the same time. (Simultaneous assignment of codes will only occur when operational requirements necessitate.)*


REFERENCE-

FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-10. STANDBY OPERATION

You may instruct an aircraft operating on an assigned code to change the transponder/ADS-B to “standby” position:

1. When approximately 15 miles from its destination and you no longer desire operation of the transponder/ADS-B; or
2. When necessary to reduce clutter in a multi-target area, provided you instruct the pilot to return the transponder/ADS-B to “normal” position as soon as possible thereafter.

   PHRASEOLOGY-

   SQUAWK STANDBY,
    or
   SQUAWK NORMAL.


   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-11. CODE MONITOR

1. Continuously monitor the codes assigned to aircraft operating within your area of responsibility. Additionally, monitor Code 1200, Code 1202, Code 1203, Code 1255, and Code 1277 unless your area of responsibility includes only Class A airspace. During periods when excessive VFR target presentations derogate the separation of IFR traffic, monitoring of the aforementioned codes may be temporarily discontinued.
2. When your area of responsibility contains or is immediately adjacent to a restricted area, warning area, VR route, or other category where Code 4000 is appropriate, monitor Code 4000 and any other code used in lieu of 4000.

   REFERENCE-

   FAA Order JO 7210.3, Para 3-6-3, Monitoring of Mode 3/A Radar Beacon Codes.

#### 5-2-12. FAILURE TO DISPLAY ASSIGNED BEACON CODE OR INOPERATIVE/MALFUNCTIONING TRANSPONDER

1. Inform an aircraft with an operable transponder that the assigned beacon code is not being displayed.

   PHRASEOLOGY-

   (Identification) RESET TRANSPONDER, SQUAWK (appropriate code).
2. Inform an aircraft when its transponder appears to be inoperative or malfunctioning.

   PHRASEOLOGY-

   (Identification) YOUR TRANSPONDER APPEARS INOPERATIVE/MALFUNCTIONING, RESET, SQUAWK (appropriate code).
3. Ensure that the subsequent control position in the facility or the next facility, as applicable, is notified when an aircraft transponder is malfunctioning/inoperative.

   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-13. INOPERATIVE OR MALFUNCTIONING INTERROGATOR

Inform aircraft concerned when the ground interrogator appears to be inoperative or malfunctioning.

PHRASEOLOGY-

(Name of facility or control function) BEACON INTERROGATOR INOPERATIVE/MALFUNCTIONING.


REFERENCE-

FAA Order JO 7110.65, Para [5-1-2](./chap5_section_1.html#aaI46JACK), ATC Surveillance Source Use.
FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-14. FAILED TRANSPONDER OR ADS-B OUT TRANSMITTER

Disapprove a request or withdraw a previously issued approval to operate with a failed transponder or ADS-B Out solely on the basis of traffic conditions or other operational factors.

REFERENCE-

FAA Order JO 7110.65, Para [5-1-2](./chap5_section_1.html#aaI46JACK), ATC Surveillance Source Use.
FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-15. VALIDATION OF MODE C ALTITUDE READOUT

1. Ensure that Mode C altitude readouts are valid after:
   1. Initial track start.
   2. Track start from coast/frozen status.
   3. During and after an unreliable Mode C readout.
   4. Accepting an interfacility handoff, except:
      1. CTRD‐equipped tower cabs are not required to validate Mode C altitude readouts after accepting interfacility handoffs from TRACONs according to the procedures in paragraph [5-4-3](./chap5_section_4.html#G7J28aJACK), Methods, subparagraph a4.
      2. ERAM facilities are not required to validate Mode C altitude readouts after accepting interfacility handoffs from other ERAM facilities, except:
         1. After initial track start or track start from coast is required, or
         2. During and after the display of a missing, unreasonable, exceptional, or otherwise unreliable Mode C readout indicator.

            NOTE-

            Consider a Mode C readout unreliable when any condition exists that indicates the Mode C may be in error, not just those that display an indicator in the Data Block.
2. Consider an altitude readout valid when:
   1. It varies less than 300 feet from the pilot reported altitude, or

      PHRASEOLOGY-

      (If aircraft is known to be operating below the lowest useable flight level),
      SAY ALTITUDE.
       or
      (If aircraft is known to be operating at or above the lowest useable flight level),
      SAY FLIGHT LEVEL.
   2. You receive a continuous readout from an aircraft on the airport and the readout varies by less than 300 feet from the field elevation, or

      NOTE-

      A continuous readout exists only when the altitude filter limits are set to include the field elevation.


      REFERENCE-

      FAA Order JO 7110.65, Para [5-2-21](#w8J2c6JACK), Altitude Filters.
      FAA Order JO 7110.65, Para [5-13-5](./chap5_section_13.html#GdI35cJACK), Selected Altitude Limits.
   3. You have correlated the altitude information in your data block with the validated information in a data block generated in another facility (by verbally coordinating with the other controller) and your readout is exactly the same as the readout in the other data block.
3. When unable to validate the readout, do not use the Mode C altitude information for separation.
4. Whenever you observe an aircraft below FL 180 with an invalid Mode C readout:
   1. Issue the correct altimeter setting and confirm the pilot has accurately reported the altitude.

      PHRASEOLOGY-

      (Location) ALTIMETER (appropriate altimeter), VERIFY ALTITUDE.
   2. If the altitude readout continues to be invalid:
      1. Instruct the pilot to turn off the altitude‐ reporting part of his/her transponder and include the reason; and
      2. Notify the operations supervisor‐in‐charge of the aircraft call sign.

         PHRASEOLOGY-

         STOP ALTITUDE SQUAWK. ALTITUDE DIFFERS BY (number of feet) FEET.
5. Whenever you observe an aircraft at or above FL 180 with an invalid Mode C readout, unless the aircraft is descending below Class A airspace:
   1. Verify that the pilot is using 29.92 inches of mercury as the altimeter setting and has accurately reported the altitude.

      PHRASEOLOGY-

      VERIFY USING TWO NINER NINER TWO AS YOUR ALTIMETER SETTING.
      (If aircraft is known to be operating at or above the lowest useable flight level),VERIFY FLIGHT LEVEL.
   2. If the Mode C readout continues to be invalid:
      1. Instruct the pilot to turn off the altitude‐ reporting part of his/her transponder and include the reason; and
      2. Notify the operations supervisor‐in‐charge of the aircraft call sign.

         PHRASEOLOGY-

         STOP ALTITUDE SQUAWK. ALTITUDE DIFFERS BY (number of feet) FEET.
6. Whenever possible, inhibit altitude readouts on all consoles when a malfunction of the ground equipment causes repeated invalid readouts.

#### 5-2-16. ALTITUDE CONFIRMATION- MODE C

Request a pilot to confirm assigned altitude on initial contact unless:

NOTE-

For the purpose of this paragraph, “initial contact” means a pilot's first radio contact with each sector/position.

1. The pilot states the assigned altitude, or
2. You assign a new altitude to a climbing or a descending aircraft, or
3. The Mode C readout is valid and indicates that the aircraft is established at the assigned altitude, or
4. TERMINAL. The aircraft was transferred to you from another sector/position within your facility (intrafacility).

   PHRASEOLOGY-

   (In level flight situations),VERIFY AT (altitude/flight level).
   (In climbing/descending situations),
   (if aircraft has been assigned an altitude below the lowest useable flight level),VERIFY ASSIGNED ALTITUDE (altitude).
    or
   (If aircraft has been assigned a flight level at or above the lowest useable flight level),
   VERIFY ASSIGNED FLIGHT LEVEL (flight level).


   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-17. ALTITUDE CONFIRMATION- NON-MODE C

1. Request a pilot to confirm assigned altitude on initial contact unless:

   NOTE-

   For the purpose of this paragraph, “initial contact” means a pilot's first radio contact with each sector/position.

   1. The pilot states the assigned altitude, or
   2. You assign a new altitude to a climbing or a descending aircraft, or
   3. TERMINAL. The aircraft was transferred to you from another sector/position within your facility (intrafacility).

      PHRASEOLOGY-

      (In level flight situations),VERIFY AT (altitude/flight level).
      (In climbing/descending situations),VERIFY ASSIGNED ALTITUDE/FLIGHT LEVEL (altitude/flight level).
2. **USA.**Reconfirm all pilot altitude read backs.

   PHRASEOLOGY-

   (If the altitude read back is correct),
   AFFIRMATIVE (altitude).
   (If the altitude read back is not correct),
   NEGATIVE. CLIMB/DESCEND AND MAINTAIN (altitude),
    or
   NEGATIVE. MAINTAIN (altitude).


   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-18. AUTOMATIC ALTITUDE REPORTING

Inform an aircraft when you want it to turn on/off the automatic altitude reporting feature of its transponder.

PHRASEOLOGY-

SQUAWK ALTITUDE,
 or
STOP ALTITUDE SQUAWK.


NOTE-

Controllers should be aware that not all aircraft have a capability to disengage the altitude squawk independently from the beacon code squawk. On some aircraft both functions are controlled by the same switch.


REFERENCE-

FAA Order JO 7110.65, Para [5-2-15](#mWK154JACK), Validation of Mode C Altitude Readout.
FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.
P/CG Term - Automatic Altitude Report.

#### 5-2-19. INFLIGHT DEVIATIONS FROM TRANSPONDER/MODE C REQUIREMENTS BETWEEN 10,000 FEET AND 18,000 FEET

Apply the following procedures to requests to deviate from the Mode C transponder requirement by aircraft operating in the airspace of the 48 contiguous states and the District of Columbia at and above 10,000 feet MSL and below 18,000 feet MSL, excluding the airspace at and below 2,500 feet AGL.

NOTE-

1. *14 CFR section 91.215(b) provides, in part, that all U.S. registered civil aircraft must be equipped with an operable, coded radar beacon transponder when operating in the altitude stratum listed above. Such transponders must have a Mode 3/A 4096 code capability, replying to Mode 3/A interrogation with the code specified by ATC, or a Mode S capability, replying to Mode 3/A interrogations with the code specified by ATC. The aircraft must also be equipped with automatic pressure altitude reporting equipment having a Mode C capability that automatically replies to Mode C interrogations by transmitting pressure altitude information in 100‐foot increments.*
2. *The exception to 14 CFR section 91.215 (b) is 14 CFR section 91.215(b)(5) which states: except balloons, gliders, and aircraft without engine‐driven electrical systems.*


REFERENCE-

FAA Order JO 7210.3, Chapter 20, Temporary Flight Restrictions.

1. Except in an emergency, do not approve inflight requests for authorization to deviate from 14 CFR section 91.215(b)(5)(i) requirements originated by aircraft without transponder equipment installed.
2. Approve or disapprove other inflight deviation requests, or withdraw approval previously issued to such flights, solely on the basis of traffic conditions and other operational factors.
3. Adhere to the following sequence of action when an inflight VFR deviation request is received from an aircraft with an inoperative transponder or Mode C, or is not Mode C equipped:
   1. Suggest that the aircraft conduct its flight in airspace unaffected by the CFRs.
   2. Suggest that the aircraft file an IFR flight plan.
   3. Suggest that the aircraft provide a VFR route of flight and maintain radio contact with ATC.
4. Do not approve an inflight deviation unless the aircraft has filed an IFR flight plan or a VFR route of flight is provided and radio contact with ATC is maintained.
5. You may approve an inflight deviation request which includes airspace outside your jurisdiction without the prior approval of the adjacent ATC sector/facility providing a transponder/Mode C status report is forwarded prior to control transfer.
6. Approve or disapprove inflight deviation requests within a reasonable period of time or advise when approval/disapproval can be expected.

   REFERENCE-

   FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-20. BEACON TERMINATION

Inform the pilot when you want their aircraft's transponder and ADS-B Out turned off.

PHRASEOLOGY-

STOP SQUAWK.
(For a military aircraft when you do not know if the military service requires that it continue operating on another mode),
STOP SQUAWK (mode in use).


REFERENCE-

FAA Order JO 7110.65, Para [5-3-3](./chap5_section_3.html#vQJ82JACK), Beacon/ADS-B Identification Methods.

#### 5-2-21. ALTITUDE FILTERS

TERMINAL

Set altitude filters to display Mode C altitude readouts to encompass all altitudes within the controller's jurisdiction. Set the upper limits no lower than 1,000 feet above the highest altitude for which the controller is responsible. In those stratified positions, set the lower limit to 1,000 feet or more below the lowest altitude for which the controller is responsible. When the position's area of responsibility includes down to an airport field elevation, the facility will normally set the lower altitude filter limit to encompass the field elevation so that provisions of paragraph [2-1-6](./chap2_section_1.html#aZI3c0JACK), Safety Alert, and paragraph [5-2-15](#mWK154JACK), Validation of Mode C Altitude Readout, subparagraph [b](#9GJf0JACK)[2](#eGJ10eJACK) may be applied. Air traffic managers may authorize temporary suspension of this requirement when target clutter is excessive.

#### 5-2-22. INOPERATIVE OR MALFUNCTIONING ADS-B TRANSMITTER

1. When an aircraft's ADS-B transmitter appears to be inoperative or malfunctioning, notify the OS/CIC of the aircraft call sign, location, and time of the occurrence (UTC). Except for DoD aircraft or those provided for in paragraph [5-2-24](#ennoT1393Shaw), inform the pilot.

   PHRASEOLOGY-

   YOUR ADS-B TRANSMITTER APPEARS TO BE INOPERATIVE / MALFUNCTIONING.


   NOTE-

   FAA Flight Standards Service, Safety Standards Division (AFS) is responsible for working with aircraft operators to correct ADS-B malfunctions. The intent of this paragraph is to capture ADS-B anomalies observed by ATC, such as errors in the data (other than Call Sign Mis‐Match events, which are detected and reported to AFS automatically) or instances when civil ADS-B transmissions would normally be expected but are not received (e.g., ADS-B transmissions were observed on a previous flight leg).
2. If a malfunctioning ADS-B transmitter is jeopardizing the safe execution of air traffic control functions, instruct the aircraft to stop ADS-B transmissions, and notify the OS/CIC.

   PHRASEOLOGY-

   STOP ADS-B TRANSMISSIONS, AND IF ABLE, SQUAWK THREE/ALFA (code).


   NOTE-

   Not all aircraft have a capability to disengage the ADS-B transmitter independently from the beacon code squawk.


   REFERENCE-

   FAA Order JO 7110.65, Para [5-2-23](#X6b8Y1dfShaw), ADS-B Alerts.
   FAA Order JO 7210.3, Para 2–1–33, Reporting Inoperative or Malfunctioning ADS-B Transmitters.
   FAA Order JO 7210.3, Para 5–4–9, ADS-B Out OFF Operations.
   FAA Order JO 7110.67, Para 19, ATC Security Procedures for ADS-B Out OFF Operations.

#### 5-2-23. ADS-B ALERTS

1. Call Sign Mis-Match (CSMM). A CSMM alert will occur when the transmitted ADS-B Flight Identification (FLT ID) does not match the flight plan aircraft identification. Inform the aircraft of the CSMM.

   PHRASEOLOGY-

   YOUR ADS-B FLIGHT ID DOES NOT MATCH YOUR FLIGHT PLAN AIRCRAFT IDENTIFICATION.
2. Duplicate ICAO Address. If the broadcast ICAO address is shared with one or more flights in the same ADS-B Service Area (regardless of altitude), and radar reinforcement is not available, target resolution may be lost on one or both targets.

   NOTE-

   Duplicate ICAO Address Alerts appear as “DA” and are associated with the Data Block (DB) on STARS systems. Duplicate ICAO Address Alerts appear as “DUP” and are associated with the DB on MEARTS systems. Duplicate ICAO Address Alerts appear as “Duplicate 24-bit Address” at the AT Specialist Workstation on ERAM systems.
3. If a CSMM or Duplicate ICAO address is jeopardizing the safe execution of air traffic control functions, instruct the aircraft to stop ADS-B transmissions, and notify the OS/CIC.

   PHRASEOLOGY-

   STOP ADS-B TRANSMISSIONS, AND IF ABLE, SQUAWK THREE/ALFA (code).


   NOTE-

   Not all aircraft are capable of disengaging the ADS-B transmitter independently from the transponder.

#### 5-2-24. ADS-B OUT OFF OPERATIONS

Operators of aircraft with functional ADS-B Out avionics installed and requesting an exception from the requirement to transmit at all times must obtain authorization from FAA System Operations Security. The OS/CIC should inform you of any ADS-B Out OFF operations in your area of jurisdiction.

1. Do not inform such aircraft that their ADS-B transmitter appears to be inoperative.
2. Do not approve any pilot request for ADS-B Out OFF operations. Notify the OS/CIC of the request, including the aircraft call sign and location.

   NOTE-

   14 CFR section 91.225(f) requires, in part, that “each person operating an aircraft equipped with ADS–B Out must operate this equipment in the transmit mode at all times unless otherwise authorized by the FAA when that aircraft is performing a sensitive government mission for national defense, homeland security, intelligence or law enforcement purposes, and transmitting would compromise the operations security of the mission or pose a safety risk to the aircraft, crew, or people and property in the air or on the ground.”


   REFERENCE-

   FAA Order JO 7110.65, Para [5-2-22](#$ynoT12e4Shaw), Inoperative or Malfunctioning ADS-B Transmitter.
   FAA Order JO 7210.3, Para 5–4–9, ADS-B Out OFF Operations.
   FAA Order JO 7110.67, Para 19, ATC Security Procedures for ADS-B Out Off Operations.
