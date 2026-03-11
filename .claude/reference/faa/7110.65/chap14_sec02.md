# 7110.65 - Chapter 14: Data Link Communications

## Section 2. En Route CPDLC Domestic

## Section 2. En Route Controller Pilot Data Link Communications (CPDLC) - Domestic

NOTE-

*Controller Pilot Data Link Communications (CPDLC) messages in use in domestic en route operations are contained in [TBL 14-2-1](#KdEye156Shaw) through [TBL 14-2-23](#PFFye1382Shaw).*

#### 14-2-1. GENERAL

1. The use of CPDLC is approved to augment the voice communication requirements of FAA Order JO 7110.65 for all altitudes, routes, speeds, holding clearances, altimeters, advisories, and frequency changes.
2. The sector team is responsible for sending and responding to CPDLC messages.
3. Controllers should minimize the use of CPDLC during critical phases of flight.
4. CPDLC should not be used to issue immediate or expeditious clearances unless voice communication is not operationally feasible.
5. Ensure there are no trajectory altering clearances (TAC) open prior to transfer of communication unless otherwise coordinated.
6. Use of the automated Voice Communication Indicator (VCI) during CPDLC operations complies with the requirements of FAA Order JO 7110.65 paragraph [2-1-17](./chap2_section_1.html#sFKbeJACK), Radio Communications.
7. Unless otherwise coordinated, the last controller working the aircraft before it exits the continental United States (U.S.) must ensure the CPDLC connection is terminated upon transfer of communication to any non‐U.S. facility or Advanced Technologies and Oceanic Procedures (ATOP) sector.
8. Coordination must be accomplished with the sector with eligibility prior to terminating a CPDLC connection from any other position or adapted air traffic workstation.
9. In the event of receipt of an emergency pilot initiated downlink (PID), follow the provisions of FAA Order JO 7110.65, [Chapter 10](./chap10_section_1.html#4B6r612e8Mary), Emergencies.
10. When responding to a PID for a weather deviation request via CPDLC, and the aircraft has a clearance to climb/descend via or has a crossing restriction, the controller must unable the request and revert to voice communications.

    NOTE-

    After a climb via or descend via clearance has been issued, a vector/deviation off a SID/STAR cancels the altitude restrictions on the procedure. The aircraft's flight management system (FMS) may be unable to process crossing altitude restrictions once the aircraft leaves the SID/STAR lateral path. Without an assigned altitude, the aircraft's FMS may revert to leveling off at the altitude set by the pilot, which may be the SID/STAR published top or bottom altitude.


    REFERENCE-

    FAA Order JO 7110.65, Para [4-2-5](./chap4_section_2.html#vUK398JACK), Route or Altitude Amendments.

#### 14-2-2. ABNORMAL SITUATIONS

1. When an Initial Contact (IC) mismatch or confirm assigned altitude (CAA) downlink time‐out indicator is displayed in the full data block (FDB) and ACL, the controller who has the aircraft on their voice frequency must use voice communication to verify the assigned altitude of the aircraft and acknowledge the IC mismatch/time‐out indicator.

   NOTE-

   All sectors in the controlling ARTCC displaying an FDB will show the IC mismatch/time‐out indicator.
2. Abnormal CPDLC indications must be acknowledged by the controller only after required coordination has been performed.
3. Use voice communications when overriding an open CPDLC clearance and issuing alternate control instructions. If the CPDLC clearance contains multiple elements, the entire clearance must be restated.

   PHRASEOLOGY-

   DISREGARD CPDLC (type) CLEARANCE (description of clearance) AND SEND AN UNABLE (alternate clearance).


   EXAMPLE-

   “American Fifty‐Two, disregard CPDLC altitude clearance to flight level three five zero and send an unable. Climb and maintain flight level three one zero.”
   “Delta Four Twenty‐Three, disregard CPDLC route clearance direct Memphis and send an unable. Cleared direct Nashville, direct Memphis, rest of route unchanged.”
   “United Thirty‐Two, disregard CPDLC hold clearance at JKSON and send an unable. Cleared to Atlanta airport via direct JKSON GLAVN one, maintain flight level three three zero.”
   “Alaska Ten, disregard CPDLC crossing and speed clearance at EMZOH and send an unable. Cross EMZOH at and maintain flight level two eight zero at two five zero knots.”


   NOTE-

   Controllers should be aware that the CPDLC clearance being overridden may not have been received on the flight deck at the time of the voice communication. This phraseology tells the pilot exactly which clearance requires an UNABLE response.
4. Controllers may cancel an open uplink only after ensuring the pilot has been issued and acknowledged, via voice communication, the superseding ATC clearance.

   NOTE-

   1. The provisions of this paragraph are not intended to replace the requirements to override a CPDLC clearance as stipulated in paragraph [14-2-3](#pREye129Shaw).
   2. Canceling an uplink only removes the uplink from the CPDLC ground system. The uplink remains open on the flight deck. Controllers must instruct the pilot to respond with an unable to close the uplink on the flight deck.
   3. The ability to cancel an uplink is only provided to allow controllers to clear open uplink indications in the FDB and ACL. Clearing these indications allows controllers to continue CPDLC operations with the affected aircraft.
5. For No Radio (NORDO) aircraft with an active CPDLC connection:
   1. It is permissible for the sector with eligibility to mark the aircraft on frequency to allow CPDLC communications with that aircraft.
   2. Use procedures in FAA Order JO 7110.65, paragraphs [5-2-4](./chap5_section_2.html#46N35cJACK), Radio Failure, and [10-4-4](./chap10_section_4.html#FLJ186JACK), Communications Failure, for all CPDLC aircraft that experience a two‐way voice radio communications failure.

#### 14-2-3. SYSTEM SITUATIONS

1. If the CPDLC system fails to provide a necessary automated altimeter setting to an aircraft, the controller must issue an altimeter setting in accordance with FAA Order JO 7110.65, [Chapter 2](./chap2_section_1.html#oa?t612e9Mary), [Section 7](./chap2_section_7.html#Gb?t6126cMary), Altimeter Settings.

   NOTE-

   If the CPDLC system fails to provide an automated altimeter setting, the controller with eligibility will be notified with an abnormal indication in the FDB. Automated altimeters are only sent in response to a monitor transfer of communication (TOC), or an altitude uplink when the assigned altitude is below FL 180.
2. When a CPDLC connection is unexpectedly lost with an aircraft, and voice communication had not previously been established, the controller must ensure voice communication is established and maintained with that aircraft.
3. Whenever there is a shutdown or failure of CPDLC service:
   1. Controllers must use voice to broadcast a message alerting pilots to the shutdown and request no pilot downlinks until further advised.

      EXAMPLE-

      “Attention all aircraft; CPDLC no longer in use. Do not downlink any messages until further advised.”
   2. Controllers must take action to ensure that any open or abnormally closed uplinks at the time of the shutdown are resolved, by voice, with each aircraft.

#### 14-2-4. SPECIFIC UPLINKS

1. Advisory Messages
   1. Control instructions and messages that require an acknowledgement from the aircraft must not be issued via advisory/free text messages.
   2. When using abbreviations to compose weather related or advisory/free text messages, comply with FAA Order JO 7340.2, Contractions.

      NOTE-

      Some common meteorological abbreviations:
       1. Extreme = EXTRM
       2. Severe = SEV
       3. Heavy = HVY
       4. Moderate = MOD
       5. Light = LGT
       6. Turbulence = TURB
       7. Continuous = CONS
       8. Occasional = OCNL
       9. Intermittent = INTMT
2. Speeds
   1. When using CPDLC to issue a speed assignment to an aircraft at or above FL 390, the WILCO response satisfies the requirement in JO 7110.65, [5-7-2](./chap5_section_7.html#qnI37aJACK)[b](./chap5_section_7.html#UZEye1142Shaw), regarding pilot concurrence.
   2. CPDLC must not be used to issue a speed adjustment to an aircraft established on a route or procedure that has published speed restrictions.
3. Holding
   1. CPDLC must not be used to clear an aircraft out of holding.

      NOTE-

      Because a route uplink does not specify a new clearance limit, clearing an aircraft out of holding must be done via voice.
   2. If an aircraft has a clearance to climb/descend via, holding instructions must not be issued via CPDLC.

      NOTE-

      The vertical navigation portion of the procedure must be canceled prior to using CPDLC to issue holding instructions.

      ***TBL 14-2-1*
      Response Attribute of CPDLC Message Element**

      |  |  |
      | --- | --- |
      | **Response Attribute** | **Description** |
      | **For Uplink Message** | |
      | W/U | Response required.  Valid responses. WILCO, UNABLE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR  NOTE– WILCO, UNABLE, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message.  FANS 1/A.— WILCO, UNABLE, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY. |
      | A/N | Response required.  Valid responses. AFFIRM, NEGATIVE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR  NOTE– AFFIRM, NEGATIVE, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message.  FANS 1/A.— AFFIRM, NEGATIVE, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY |
      | R | Response required.  Valid responses. ROGER, UNABLE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR  NOTE– ROGER, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message.  FANS 1/A.— ROGER, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY. FANS 1/A aircraft do not have the capability to send UNABLE in response to an uplink message containing message elements with an “R” response attribute. For these aircraft, the flight crew may use alternative means to UNABLE the message. These alternative means will need to be taken into consideration to ensure proper technical and operational closure of the communication transaction. |  |
      | Y | Response required.  Valid responses: Any CPDLC downlink message, LOGICAL ACKNOWLEDGEMENT (only if required). |
      | N | No response required unless logical acknowledgement is required.  Valid Responses (only if LOGICAL ACKNOWLEDGEMENT is required). LOGICAL ACKNOWLEDGEMENT, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, ERROR  FANS 1/A.— “N” is defined as “no response is required,” but not used. Under some circumstances, an ERROR message will also close an uplink message. |
      | NE | [Not defined in Doc 4444]  FANS 1/A.— The WILCO, UNABLE, AFFIRM, NEGATIVE, ROGER, and STANDBY responses are not enabled (NE) for flight crew selection. An uplink message with a response attribute NE is considered to be closed even though a response may be required operationally. Under some circumstances, a downlink error message may be linked to an uplink message with a NE attribute. |
      | **For Downlink Message** | |
      | Y | Response required. Yes  Valid responses. Any CPDLC uplink message, LOGICAL ACKNOWLEDGEMENT (only if required). |
      | N | Response required. No, unless logical acknowledgement required.  Valid responses (only if LOGICAL ACKNOWLEDGEMENT is required). LOGICAL ACKNOWLEDGEMENT, SERVICE UNAVAILABLE, FLIGHT PLAN NOT HELD, ERROR  FANS 1/A.— Aircraft do not have the capability to receive technical responses to downlink message elements with an “N” response attribute (other than LACK or ERROR for ATN B1 aircraft). In some cases, the response attribute is different between FANS 1/A aircraft and Doc 4444. As an example, most emergency messages have an “N” response attribute for FANS 1/A whereas Doc 4444 defines a “Y” response attribute for them. As a consequence, for FANS 1/A aircraft, ATC will need to use alternative means to acknowledge to the flight crew that an emergency message has been received. |

      ***TBL 14-2-2*
      Route Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
      | UM74 | position | W/U | Instruction to proceed directly to the specified position. |  |
      | UM75 | position  NOTE– This message element is equivalent to SUPU-5 plus RTEU-2 in Doc 4444. | W/U | Instruction to proceed directly to the specified position. |
      | UM77 | AT (*position*) PROCEED DIRECT TO (*position*) | W/U | Instruction to proceed, at the specified position, directly to the next specified position. |
      | UM78 | AT *(altitude)* PROCEED DIRECT TO (*position*) | W/U | Instruction to proceed directly to the specified position upon reaching the specified altitude. |
      | UM79 | CLEARED TO (*position*) VIA (*route clearance*) | W/U | Instruction to proceed to the specified position via the specified route. |
      | UM80 | route clearance | W/U | Instruction to proceed via the specified route. |
      | UM83 | AT (*position*) CLEARED (*route clearance*) | W/U | Instruction to proceed from the specified position via the specified route. |
      | UM91 | HOLD AT (*position*) MAINTAIN *(altitude)* INBOUND TRACK (*degrees*) (*direction*) TURN LEG TIME (*leg type*) | W/U | Instruction to enter a holding pattern at the specified position in accordance with the specified instructions.  NOTE– RTEU-13 EXPECT FURTHER CLEARANCE AT (time) is appended to this message when an extended hold is anticipated. |
      | UM92 | HOLD AT (*position*) AS PUBLISHED MAINTAIN *(altitude)* | W/U | Instruction to enter a holding pattern at the specified position in accordance with the published holding instructions.  NOTE– RTEU-13 EXPECT FURTHER CLEARANCE AT TIME (time) is appended to this message when an extended hold is anticipated. |
      | UM93 | time | W/U | Notification that an onwards clearance may be issued at the specified time. |
      | UM137 | CONFIRM ASSIGNED ROUTE  NOTE– NE response attribute. | Y | Request to confirm the assigned route. |

      ***TBL 14-2-3*
      Route Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
      | DM22 | position | Y | Request for a direct clearance to the specified position. |  |
      | DM23 | procedure name | Y | Request for the specified procedure or clearance name. |
      | DM24 | route clearance | Y | Request for the specified route. |
      | DM40 | route clearance | N | Confirmation that the assigned route is the specified route. |

      ***TBL 14-2-4*
      Lateral Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
      | UM82 | CLEARED TO DEVIATE UP TO (*distance offset*) (*direction*) OF ROUTE | W/U | Instruction allowing deviation up to the specified distance(s) from the cleared route in the specified direction(s). |
      | UM127 | REPORT BACK ON ROUTE  NOTE– R response attribute. | W/U | Instruction to report when the aircraft is back on the cleared route. |

      ***TBL 14-2-5*
      Lateral Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
      | DM27 | REQUEST WEATHER DEVIATION UP TO (*specified distance*) (*direction*) OF ROUTE | Y | Request for a weather deviation up to the specified distance(s) off track in the specified direction(s). |
      | DM41 | BACK ON ROUTE | N | Report indicating that the cleared route has been rejoined. |
      | DM59 | DIVERTING TO (*position*) VIA (*route clearance*)  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N  See Note | Report indicating diverting to the specified position via the specified route, which may be sent without any previous coordination done with ATC. |
      | DM60 | OFFSETTING (*distance* *offset*) (*direction*) OF ROUTE  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N  See Note | Report indicating that the aircraft is offsetting to a parallel track at the specified distance in the specified direction off from the cleared route. |
      | DM80 | DEVIATING (*deviation* *offset*) (*direction*) OF ROUTE  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N  See Note | Report indicating deviating specified distance or degrees in the specified direction from the cleared route. |

      NOTE-

      ICAO Document 10037, Global Operational Data Link (GOLD) Manual, has these values set to Y in their table.

      ***TBL 14-2-6*
      Altitude Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM19 | (altitude) | W/U | Instruction to maintain the specified altitude. |
      | UM20 | (altitude) | W/U | Instruction that a climb to the specified altitude is to commence and once reached is to be maintained. |
      | UM23 | (altitude) | W/U | Instruction that a descent to the specified altitude is to commence and once reached is to be maintained. |
      | UM30 | MAINTAIN BLOCK *(altitude)* TO *(altitude)* | W/U | Instruction to maintain the specified vertical range. |
      | UM31 | CLIMB TO AND MAINTAIN BLOCK *(altitude)* TO *(altitude)* | W/U | Instruction that a climb to the specified vertical range is to commence and once reached is to be maintained. |
      | UM32 | DESCEND TO AND MAINTAIN BLOCK *(altitude)* TO *(altitude)* | W/U | Instruction that a descent to the specified vertical range is to commence and once reached is to be maintained. |
      | UM36 | (altitude)  NOTE– This message element is equivalent to SUPU-3 plus LVLU-6 in Doc 4444. | W/U | Instruction that a climb to the specified altitude or vertical range is to commence and once reached is to be maintained. |
      | UM37 | (altitude) | W/U | Instruction that a descent to the specified altitude or vertical range is to commence and once reached is to be maintained. |
      | UM38 | (altitude)  NOTE– This message element is equivalent to EMGU-2 plus LVLU-6 in Doc 4444. | W/U | Instruction that a climb to the specified altitude or vertical range is to commence and once reached is to be maintained. |
      | UM39 | (altitude)  NOTE– This message element is equivalent to EMGU-2 plus LVLU-9 in Doc 4444. | W/U | Instruction that a descent to the specified altitude or vertical range is to commence and once reached is to be maintained. |
      | UM135 | CONFIRM ASSIGNED ALTITUDE  NOTE– NE response attribute. | Y | Request to confirm the assigned altitude. |
      | UM177 | AT PILOTS DISCRETION  See Note | NE | An instruction used in conjunction with altitude assignments, means that ATC has offered the pilot the option of starting climb or descent whenever they wish and conducting the climb or descent at any rate they wish. The pilot may temporarily level off at any intermediate altitude. However, once the aircraft has vacated an altitude, it may not return to that altitude. |

      NOTE-

      ICAO Document 10037, Global Operational Data Link (GOLD) Manual, does not include this in its tables.

      ***TBL 14-2-7*
      Altitude Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM6 | (altitude) | Y | Request to fly at the specified altitude. |
      | DM7 | REQUEST BLOCK *(altitude)* TO *(altitude)* | Y | Request to fly at the specified vertical range. |
      | DM9 | (altitude) | Y | Request for a climb to the specified level or vertical range. |
      | DM10 | (altitude) | Y | Request for a descent to the specified level or vertical range. |
      | DM38 | (altitude) | N | Confirmation that the assigned altitude is the specified altitude or vertical range. |
      | DM61 | DM61 DESCENDING TO (altitude)  NOTE– Urgent alert attribute. | N | Report indicating descending to the specified altitude. |
      | DM77 | DM77 ASSIGNED BLOCK (*altitude*) TO (*altitude*) | N | Confirmation that the assigned vertical range is the specified vertical range. |

      ***TBL 14-2-8*
      Crossing Constraint Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM46 | CROSS (*position*) AT (*altitude*) | W/U | Instruction that the specified position is to be crossed at the specified altitude. |
      | UM49 | CROSS (*position*) AT AND MAINTAIN (*altitude*)  NOTE– This message element is equivalent to CSTU-1 plus LVLU-5 in Doc 4444. | W/U | Instruction that the specified position is to be crossed at the specified altitude. |
      | UM51 | CROSS (*position*) AT (*time*) | W/U | Instruction that the specified position is to be crossed at the specified time. |
      | UM52 | CROSS (*position*) AT OR BEFORE (*time*) | W/U | Instruction that the specified position is to be crossed before the specified time. |
      | UM53 | CROSS (*position*) AT OR AFTER (*time*) | W/U | Instruction that the specified position is to be crossed after the specified time. |
      | UM55 | CROSS (*position*) AT (*speed*) | W/U | Instruction that the specified position is to be crossed at the specified speed. |
      | UM56 | CROSS (*position*) AT OR LESS THAN (*speed*) | W/U | Instruction that the specified position is to be crossed at or less than the specified speed. |  |
      | UM57 | CROSS (*position*) AT OR GREATER THAN (*speed*) | W/U | Instruction that the specified position is to be crossed at or greater than the specified speed. |
      | UM61 | CROSS (*position*) AT AND MAINTAIN (*altitude*) AT (*speed*)  NOTE– This message element is equivalent to CSTU-14 plus LVLU-5 in Doc 4444. | W/U | Instruction that the specified position is to be crossed at the specified altitude and speed. |

      ***TBL 14-2-9*
      Speed Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM106 | speed | W/U | Instruction to maintain the specified speed. |
      | UM107 | MAINTAIN PRESENT SPEED | W/U | Instruction to maintain the present speed. |
      | UM108 | speed | W/U | Instruction to maintain the specified speed or greater. |
      | UM109 | speed | W/U | Instruction to maintain the specified speed or less. |
      | UM116 | RESUME NORMAL SPEED | W/U | Instruction to resume a normal speed. The aircraft no longer needs to comply with a previously issued speed restriction. |
      | UM134 | CONFIRM SPEED  NOTE– NE response attribute. | Y | Request to report the speed defined by the speed type(s). |

      ***TBL 14-2-10*
      Speed Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM34 | speed | N | Report indicating the speed defined by the specified speed types is the specified speed. |

      ***TBL 14-2-11*
      Air Traffic Advisory Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM154 | RADAR SERVICES TERMINATED | R | Advisory that the ATS surveillance service is terminated. |  |

      ***TBL 14-2-12*
      Voice Communications Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM117 | CONTACT (*ICAO unit name*) (*frequency*) | W/U | Instruction to establish voice contact with the specified ATS unit on the specified frequency. |
      | UM120 | MONITOR (*ICAO unit name*) (*frequency*) | W/U | Instruction to monitor the specified ATS unit on the specified frequency. The flight crew is not required to establish voice contact on the frequency. |

      ***TBL 14-2-13*
      Voice Communications Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM20 | REQUEST VOICE CONTACT  NOTE– Used when a frequency is not required. | Y | Request for voice contact on the specified frequency. |

      ***TBL 14-2-14*
      Emergency/Urgency Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM38 | altitude  Used in combination with LVLU-6 and LVLU-9, which is implemented in FANS 1/A as above | Y | Instruction to immediately comply with the associated instruction to avoid imminent situation. |
      | UM39 | altitude  Used in combination with LVLU-6 and LVLU-9, which is implemented in FANS 1/A as above | Y | Instruction to immediately comply with the associated instruction to avoid imminent situation. |

      ***TBL 14-2-15*
      Emergency/Urgency Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM55 | PAN PAN PAN  NOTE– N response attribute. | Y | Indication of an urgent situation. |  |
      | DM56 | MAYDAY MAYDAY MAYDAY  NOTE– N response attribute. | Y | Indication of an emergency situation. |
      | DM57 | (*remaining fuel*) OF FUEL REMAINING AND (*remaining souls*) SOULS ON BOARD  NOTE– N response attribute | Y | Report indicating fuel remaining (time) and number of persons on board. |
      | DM58 | CANCEL EMERGENCY  NOTE– N response attribute. | Y | Indication that the emergency situation is canceled. |

      ***TBL 14-2-16*
      Standard Response Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM0 | UNABLE | N | Indication that the message cannot be complied with. |
      | UM1 | STANDBY | N | Indication that the message will be responded to shortly. |
      | UM3 | ROGER | N | Indication that the message is received. |

      ***TBL 14-2-17*
      Standard Response Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM0 | WILCO | N | Indication that the instruction is understood and will be complied with. |
      | DM1 | UNABLE | N | Indication that the instruction cannot be complied with. |
      | DM2 | STANDBY | N | Indication that the message will be responded to shortly. |
      | DM3 | ROGER  NOTE– ROGER is the only correct response to an uplink free text message. | N | Indication that the message is received. |

      ***TBL 14-2-18*
      Supplemental Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM166 | DUE TO TRAFFIC | N | Indication that the associated message is issued due to the specified reason. |  |
      | UM167 | DUE TO AIRSPACE RESTRICTION | N | Indication that the associated message is issued due to the specified reason. |

      ***TBL 14-2-19*
      Supplemental Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM65 | DUE TO WEATHER | N | Indication that the associated message is issued due to specified reason. |
      | DM66 | DUE TO AIRCRAFT PERFORMANCE | N | Indication that the associated message is issued due to specified reason. |

      ***TBL 14-2-20*
      Free Text Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM169 | free text | R | A message or part of a message that does not conform to any standard message element in the PANSATM (Doc 4444). |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |
      | UM169 | free text | R | See Note |

      NOTE-

      These are FAA scripted free text messages with no GOLD equivalent.

      ***TBL 14-2-21*
      Free Text Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM68 | free text  NOTE 1. – Urgency or Distress (M alert attribute)  NOTE 2. – Selecting any of the emergency message elements will result in this message element being enabled for the flight crew to include in the emergency message at their discretion. | Y | N/A |

      ***TBL 14-2-22*
      System Management Uplink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | UM159 | error information | N | System‐generated notification of an error. |
      | UM160 | ICAO facility designation  NOTE– The facility designation is required. | N | System‐generated notification of the next data authority or the cancellation thereof. |

      ***TBL 14-2-23*
      System Management Downlink Message Elements**

      |  |  |  |  |
      | --- | --- | --- | --- |
      | **FANS 1/A Message Identifier** | **Message Content** | **Response Attribute** | **Message element intended use** |
      | DM62 | error information | N | System‐generated notification of an error. |  |
      | DM63 | NOT CURRENT DATA AUTHORITY | N | System‐generated rejection of any CPDLC message sent from a ground facility that is not the current data authority. |
      | DM64 | ICAO facility designation  NOTE– Use by FANS 1/A aircraft in B1 environments. | N | System‐generated notification that the ground system is not designated as the next data authority (NDA), indicating the identity of the current data authority (CDA). Identity of the NDA, if any, is also reported. |
