# 7110.65 - Chapter 14: Data Link Communications

## Section 3. ATOP Oceanic CPDLC

## Section 3. Advanced Technologies and Oceanic Procedures (ATOP) - Oceanic Controller Pilot Data Link Communications (CPDLC)

NOTE-

*Controller Pilot Data Link Communications (CPDLC) messages in use in Oceanic operations are contained in [TBL 14-3-1](#M$Jye12faShaw) through [TBL 14-3-26](#bDLye145Shaw).*

#### 14-3-1. MEANS OF COMMUNICATION

1. When CPDLC is available and CPDLC connected aircraft are operating outside of VHF coverage, CPDLC must be used as the primary means of communication.
2. Voice communications may be utilized for CPDLC aircraft when it will provide an operational advantage and/or when workload or equipment capabilities demand.
3. When CPDLC is being utilized, a voice backup must exist (e.g., HF, SATCOM, Third Party).
4. When a pilot communicates via CPDLC, the response should be via CPDLC.
5. To the extent possible, the CPDLC message set should be used in lieu of free text messages.

   NOTE-

   1. The CPDLC message sets are contained in [TBL 14-3-1](#M$Jye12faShaw) through [TBL 14-3-26](#bDLye145Shaw).
   2. The use of the CPDLC message set ensures the proper “closure” of CPDLC exchanges.

#### 14-3-2. TRANSFER OF COMMUNICATIONS TO THE NEXT FACILITY

1. When the receiving facility is capable of CPDLC communications, the data link transfer is automatic and is accomplished within facility adapted parameters.
2. When a receiving facility is not CPDLC capable, the transfer of communications must be made in accordance with local directives and Letters of Agreement (LOAs).

#### 14-3-3. ABNORMAL CONDITIONS

1. If any portion of the automated transfer fails, the controller should attempt to initiate the transfer manually. If unable to complete the data link transfer, the controller should advise the pilot to log on to the next facility and send an End Service (EOS) message.
2. If CPDLC fails, voice communications must be utilized until CPDLC connections can be reestablished.
3. If the CPDLC connection is lost on a specific aircraft, the controller should send a connection request message (CR1) or advise the pilot via backup communications to log on again.
4. If CPDLC service is to be canceled, the controller must advise the pilot as early as possible to facilitate a smooth transition to voice communications. Workload permitting, the controller should also advise the pilot of the reason for the termination of data link.
5. When there is uncertainty that a clearance was delivered to an aircraft via CPDLC, the controller must continue to protect the airspace associated with the clearance until an appropriate operational response is received from the flight crew. If an expected operational response to a clearance is not received, the controller will initiate appropriate action to ensure that the clearance was received by the flight crew. On initial voice contact with aircraft preface the message with the following:

   PHRASEOLOGY-

   (Call Sign) CPDLC Failure, (message).

   ***TBL 14-3-1*
   Response Attribute of CPDLC Message Element**

   |  |  |
   | --- | --- |
   | **Response Attribute** | **Description** |
   | **For Uplink Message** | |
   | W/U | Response required.  Valid responses. WILCO, UNABLE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR.  NOTE– WILCO, UNABLE, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message.  FANS 1/A.— WILCO, UNABLE, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY. |
   | A/N | Response required.  Valid responses. AFFIRM, NEGATIVE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR  NOTE– AFFIRM, NEGATIVE, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message. FANS 1/A.— AFFIRM, NEGATIVE, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY. |  |
   | R | Response required.  Valid responses. ROGER, UNABLE, STANDBY, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, LOGICAL ACKNOWLEDGEMENT (only if required), ERROR.  NOTE– ROGER, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY and ERROR will close the uplink message.  FANS 1/A.— ROGER, STANDBY, ERROR, NOT CURRENT DATA AUTHORITY. FANS 1/A aircraft do not have the capability to send UNABLE in response to an uplink message containing message elements with an “R” response attribute. For these aircraft, the flight crew may use alternative means to UNABLE the message. These alternative means will need to be taken into consideration to ensure proper technical and operational closure of the communication transaction. |
   | Y | Response required.  Valid responses: Any CPDLC downlink message, LOGICAL ACKNOWLEDGEMENT (only if required). |
   | N | No response required unless logical acknowledgement is required.  Valid Responses (only if LOGICAL ACKNOWLEDGEMENT is required). LOGICAL ACKNOWLEDGEMENT, NOT CURRENT DATA AUTHORITY, NOT AUTHORIZED NEXT DATA AUTHORITY, ERROR.  FANS 1/A.— “N” is defined as “no response is required,” but not used. Under some circumstances, an ERROR message will also close an uplink message. |
   | NE | [Not defined in Doc 4444]  FANS 1/A.— The WILCO, UNABLE, AFFIRM, NEGATIVE, ROGER, and STANDBY responses are not enabled (NE) for flight crew selection. An uplink message with a response attribute NE is considered to be closed even though a response may be required operationally. Under some circumstances, a downlink error message may be linked to an uplink message with a NE attribute. |
   | **For Downlink Message** | |
   | Y | Response required. Yes  Valid responses. Any CPDLC uplink message, LOGICAL ACKNOWLEDGEMENT (only if required). |
   | N | Response required. No, unless logical acknowledgement required.  Valid responses (only if LOGICAL ACKNOWLEDGEMENT is required). LOGICAL ACKNOWLEDGEMENT, SERVICE UNAVAILABLE, FLIGHT PLAN NOT HELD, ERROR  FANS 1/A.— Aircraft do not have the capability to receive technical responses to downlink message elements with an “N” response attribute (other than LACK or ERROR for ATN B1 aircraft). In some cases, the response attribute is different between FANS 1/A aircraft and Doc 4444. As an example, most emergency messages have an “N” response attribute for FANS 1/A whereas Doc 4444 defines a “Y” response attribute for them. As a consequence, for FANS 1/A aircraft, ATC will need to use alternative means to acknowledge to the flight crew that an emergency message has been received. |

   ***TBL 14-3-2*
   Route Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM74 | (position) | W/U | Instruction to proceed directly to the specified position. |  |
   | UM75 | position  NOTE– This message element is equivalent to SUPU-5 plus RTEU-2 in Doc 4444. | W/U | Instruction to proceed directly to the specified position. |
   | UM76 | AT (*time*) PROCEED DIRECT TO (*position*) | W/U | Instruction to proceed, at the specified time, directly to the specified position. |
   | UM77 | AT (*position*) PROCEED DIRECT TO (*position*) | W/U | Instruction to proceed, at the specified position, directly to the next specified position. |
   | UM78 | AT (*altitude*) PROCEED DIRECT TO (*position*) | W/U | Instruction to proceed upon reaching the specified altitude, directly to the specified position. |
   | UM79 | CLEARED TO *(position)* VIA *(route clearance)* | W/U | Instruction to proceed to the specified position via the specified route. |
   | UM80 | (route clearance) | W/U | Instruction to proceed via the specified route. |
   | UM83 | AT *(position)* CLEARED *(route clearance)* | W/U | Instruction to proceed from the specified position via the specified route. |
   | UM85 | route clearance | R | Notification that a clearance to fly on the specified route may be issued. |
   | UM86 | AT (*position*) EXPECT (*route clearance*) | R | Notification that a clearance to fly on the specified route from the specified position may be issued. |
   | UM87 | position | R | Notification that a clearance to fly directly to the specified position may be issued. |
   | UM88 | AT (*position*) EXPECT DIRECT TO (*position*) | R | Notification that a clearance to fly directly from the first specified position to the next specified position may be issued. |
   | UM89 | AT (*time*) EXPECT DIRECT TO (*position*) | R | Notification that a clearance to fly directly to the specified position commencing at the specified time may be issued. |
   | UM90 | AT (*altitude*) EXPECT DIRECT TO (*position*) | R | Notification that a clearance to fly directly to the specified position commencing when the specified altitude is reached may be issued. |
   | UM93 | time | R | Notification that an onwards clearance may be issued at the specified time. |
   | UM99 | procedure name  NOTE– Used when a published procedure is designated. | R | Notification that a clearance may be issued for the aircraft to fly the specified procedure or clearance name. |
   | UM137 | CONFIRM ASSIGNED ROUTE  NOTE– NE response attribute. | NE | Request to confirm the assigned route. |  |
   | UM147 | REQUEST POSITION REPORT | NE | Request to make a position report. |

   ***TBL 14-3-3*
   Route Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM22 | REQUEST DIRECT TO *(position)* | Y | Request for a direct clearance to the specified position. |
   | DM23 | procedure name | Y | Request for the specified procedure or clearance name. |
   | DM24 | route clearance | Y | Request for the specified route. |
   | DM25 | REQUEST CLEARANCE | Y | Request for the specified clearance. |
   | DM26 | REQUEST WEATHER DEVIATION TO (*position*) VIA (*route clearance*) | Y | Request for a weather deviation to the specified position via the specified route. |
   | DM40 | route clearance | N | Confirmation that the assigned route is the specified route. |
   | DM48 | position report | N | Position report. |
   | DM51 | WHEN CAN WE EXPECT BACK ON ROUTE | Y | Request for the time or position that can be expected to rejoin the cleared route. |
   | DM70 | degrees | Y | Request for the specified heading. |
   | DM71 | degrees | Y | Request for the specified ground track. |

   ***TBL 14-3-4*
   Lateral Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM64 | OFFSET (*distance offset*) (*direction*) OF ROUTE | W/U | Instruction to fly a parallel track to the cleared route at a displacement of the specified distance in the specified direction. |
   | UM65 | AT (*position*) OFFSET (*distance offset*) (*direction*) OF ROUTE | W/U | Instruction to fly a parallel track to the cleared route at a displacement of the specified distance in the specified direction and commencing at the specified position. |
   | UM66 | AT (*time*) OFFSET (*distance offset*) (*direction*) OF ROUTE | W/U | Instruction to fly a parallel track to the cleared route at a displacement of the specified distance in the specified direction and commencing at the specified time. |
   | UM67 | PROCEED BACK ON ROUTE | W/U | Instruction to rejoin the cleared route. |  |
   | UM68 | position | W/U | Instruction to rejoin the cleared route before passing the specified position. |
   | UM69 | time | W/U | Instruction to rejoin the cleared route before the specified time. |
   | UM70 | position | W/U | Notification that a clearance may be issued to enable the aircraft to rejoin the cleared route before passing the specified position. |
   | UM71 | time | W/U | Notification that a clearance may be issued to enable the aircraft to rejoin the cleared route before the specified time. |
   | UM72 | RESUME OWN NAVIGATION | W/U | Instruction to resume own navigation following a period of tracking or heading clearances. May be used in conjunction with an instruction on how or where to rejoin the cleared route. |
   | UM82 | CLEARED TO DEVIATE UP TO (*distance offset*) (*direction*) OF ROUTE | W/U | Instruction allowing deviation up to the specified distance(s) from the cleared route in the specified direction(s). |
   | UM98 | IMMEDIATELY TURN (*direction*) HEADING (*degrees*)  NOTE– This message element is equivalent to EMGU-2 plus LATU-11 in Doc 4444. | W/U | Instruction to turn left or right as specified on to the specified heading. |
   | UM127 | REPORT BACK ON ROUTE  NOTE– R response attribute. | W/U | Instruction to report when the aircraft is back on the cleared route. |
   | UM130 | position  NOTE– R response attribute. | W/U | Instruction to report upon passing the specified position. |
   | UM132 | CONFIRM POSITION | NE | Instruction to report the present position. |
   | UM138 | CONFIRM TIME OVER REPORTED WAYPOINT | NE | Instruction to confirm the previously reported time over the last reported waypoint. |
   | UM139 | CONFIRM REPORTED WAYPOINT | NE | Instruction to confirm the identity of the previously reported waypoint. |
   | UM140 | CONFIRM NEXT WAYPOINT | NE | Instruction to confirm the identity of the next waypoint. |
   | UM141 | CONFIRM NEXT WAYPOINT ETA | NE | Instruction to confirm the previously reported estimated time at the next waypoint. |
   | UM142 | CONFIRM ENSUING WAYPOINT | NE | Instruction to confirm the identity of the next plus one waypoint. |
   | UM145 | CONFIRM HEADING | NE | Instruction to report the present heading. |
   | UM146 | REPORT GROUND TRACK | NE | Instruction to report the present ground track. |
   | UM152 | WHEN CAN YOU ACCEPT (*specified distance*) (*direction*) OFFSET | NE | Instruction to report the earliest time when the specified offset track can be accepted. |

   ***TBL 14-3-5*
   Lateral Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM15 | REQUEST OFFSET (*specified distance*) (*direction*) OF ROUTE | Y | Request for a parallel track from the cleared route at a displacement of the specified distance in the specified direction. |  |
   | DM16 | AT (*position*) REQUEST OFFSET (*specified distance*) (*direction*) OF ROUTE | Y | Request that a parallel track, offset from the cleared track by the specified distance in the specified direction, be approved from the specified position. |
   | DM17 | AT (*time*) REQUEST OFFSET (*specified distance*) (*direction*) OF ROUTE | Y | Request that a parallel track, offset from the cleared track by the specified distance in the specified direction, be approved from the specified time. |
   | DM27 | REQUEST WEATHER DEVIATION UP TO (*specified distance*) (*direction*) OF ROUTE | Y | Request for a weather deviation up to the specified distance(s) off track in the specified direction(s). |
   | DM31 | position | N | Report indicating passing the specified position. |
   | DM33 | position | N | Notification of the present position. |
   | DM35 | degrees | N | Notification of the present heading in degrees. |
   | DM36 | degrees | N | Notification of the present ground track in degrees. |
   | DM41 | BACK ON ROUTE | N | Report indicating that the cleared route has been rejoined. |
   | DM42 | position | N | The next waypoint is the specified position. |
   | DM43 | time | N | The ETA at the next waypoint is as specified. |
   | DM44 | position | N | The next plus one waypoint is the specified position. |
   | DM45 | position | N | Clarification of previously reported waypoint passage. |
   | DM46 | time | N | Clarification of time over previously reported waypoint. |
   | DM59 | DIVERTING TO *(position)* VIA *(route clearance)*  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N | Report indicating diverting to the specified position via the specified route, which may be sent without any previous coordination done with ATC. |
   | DM60 | OFFSETTING *(distance offset)* *(direction)* OF ROUTE  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N | Report indicating that the aircraft is offsetting to a parallel track at the specified distance in the specified direction off from the cleared route. |
   | DM80 | DEVIATING *(deviation offset)* *(direction)* OF ROUTE  NOTE 1. – H alert attribute.  NOTE 2. – N response attribute. | N | Report indicating deviating specified distance or degrees in the specified direction from the cleared route. |
   | DM67 | WE CAN ACCEPT (*direction*) (*distance offset*) AT (*time*) | N | We can accept a parallel track offset the specified distance in the specified direction at the specified time. |  |
   | DM67 | WE CANNOT ACCEPT (*direction*) (*distance offset*) | N | We cannot accept a parallel track offset the specified distance in the specified direction. |
   | DM67 | altitude | N | We cannot accept the specified altitude. |

   ***TBL 14-3-6*
   Altitude Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM6 | (altitude) | R | Notification that an altitude change instruction should be expected. |
   | UM7 | (time) | R | Notification that an instruction may be expected for the aircraft to commence climb at the specified time. |
   | UM8 | position | R | Notification that an instruction may be expected for the aircraft to commence climb at the specified position. |
   | UM9 | time | R | Notification that an instruction may be expected for the aircraft to commence descent at the specified time. |
   | UM10 | position | R | Notification that an instruction may be expected for the aircraft to commence descent at the specified position. |
   | UM11 | (time) | R | Notification that an instruction should be expected for the aircraft to commence cruise climb at the specified time. |
   | UM12 | position | R | Notification that an instruction should be expected for the aircraft to commence cruise climb at the specified position. |
   | UM13 | AT (*time*) EXPECT CLIMB TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence climb at the specified time to the specified altitude. |
   | UM14 | AT (*position*) EXPECT CLIMB TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence climb at the specified position to the specified altitude. |
   | UM15 | AT (*time*) EXPECT DESCENT TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence descent at the specified time to the specified altitude. |
   | UM16 | AT (*position*) EXPECT DESCENT TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence descent at the specified position to the specified altitude. |  |
   | UM17 | AT (*time*) EXPECT CRUISE CLIMB TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence cruise climb at the specified time to the specified altitude. |
   | UM18 | AT (*position*) EXPECT CRUISE CLIMB TO (*altitude*) | R | Notification that an instruction should be expected for the aircraft to commence cruise climb at the specified position to the specified altitude. |
   | UM19 | (altitude) | W/U | Instruction to maintain the specified altitude. |
   | UM20 | (altitude) | W/U | Instruction that a climb to the specified altitude is to commence and once reached is to be maintained. |
   | UM21 | AT (*time*) CLIMB TO AND MAINTAIN (*altitude*) | W/U | Instruction that at the specified time a climb to the specified altitude is to commence and once reached is to be maintained.  NOTE– This message element would be preceded with uM19 MAINTAIN (altitude) to prevent the premature execution of the instruction. |
   | UM22 | AT (*position*) CLIMB TO AND MAINTAIN (*altitude*) | W/U | Instruction that at the specified position a climb to the specified altitude is to commence and once reached is to be maintained.  NOTE– This message element would be preceded with uM19 MAINTAIN (altitude) to prevent the premature execution of the instruction. |
   | UM23 | (altitude) | W/U | Instruction that a descent to the specified altitude is to commence and once reached is to be maintained. |
   | UM24 | AT (*time*) DESCEND TO AND MAINTAIN (*altitude*) | W/U | Instruction that at the specified time a descent to the specified altitude is to commence and once reached is to be maintained. |
   | UM25 | AT (*position*) DESCEND TO AND MAINTAIN (*altitude*) | W/U | Instruction that at the specified position a descent to the specified altitude is to commence and once reached is to be maintained. |
   | UM26 | CLIMB TO REACH (*altitude*) BY (*time*) | W/U | Instruction that a climb is to be completed such that the specified altitude is reached before the specified time. |
   | UM27 | CLIMB TO REACH (*altitude*) BY (*position*) | W/U | Instruction that a climb is to be completed such that the specified altitude is reached before passing the specified position. |
   | UM28 | DESCEND TO REACH (*altitude*) BY (*time*) | W/U | Instruction that a descent is to be completed such that the specified altitude is reached before the specified time. |
   | UM29 | DESCEND TO REACH (*altitude*) BY (*position*) | W/U | Instruction that a descent is to be completed such that the specified altitude is reached before passing the specified position. |  |
   | UM30 | MAINTAIN BLOCK (*altitude*) TO (*altitude*) | W/U | Instruction to maintain the specified vertical range. |
   | UM31 | CLIMB TO AND MAINTAIN BLOCK | W/U | Instruction that a climb to the specified vertical range is to commence and once reached is to be maintained. |
   | UM32 | DESCEND TO AND MAINTAIN BLOCK (*altitude*) TO (*altitude*) | W/U | Instruction that a descent to the specified vertical range is to commence and once reached is to be maintained. |
   | UM33 | altitude |  | Instruction that authorizes a pilot to conduct flight at any altitude from the minimum altitude up to and including the altitude specified in the clearance. Further, it is approval for the pilot to proceed to and make an approach at the destination airport. |
   | UM34 | altitude | W/U | A cruise climb is to commence and continue until the specified altitude is reached. |
   | UM35 | altitude | W/U | A cruise climb can commence once above the specified altitude. |
   | UM36 | altitude | W/U | Instruction that a climb to the specified altitude or vertical range is to commence and once reached is to be maintained. |
   | UM37 | (altitude) | W/U | Instruction that a descent to the specified altitude or vertical range is to commence and once reached is to be maintained. |
   | UM38 | altitude | W/U | Instruction that a climb to the specified altitude or vertical range is to commence and once reached is to be maintained. |
   | UM39 | altitude | W/U | Instruction that a descent to the specified altitude or vertical range is to commence and once reached is to be maintained. |
   | UM40 | altitude | W/U | Urgent instruction to immediately stop a climb once the specified altitude is reached. |
   | UM41 | altitude | W/U | Urgent instruction to immediately stop a descent once the specified altitude is reached. |
   | UM128 | altitude  NOTE– R response attribute. | W/U | Instruction to report upon leaving the specified altitude. |
   | UM129 | altitude  NOTE– R response attribute. | W/U | Instruction to report upon maintaining the specified altitude. |
   | UM133 | CONFIRM ALTITUDE | NE | Instruction to report the present altitude. |  |
   | UM135 | CONFIRM ASSIGNED ALTITUDE  NOTE– NE response attribute. | NE | Request to confirm the assigned altitude. |
   | UM148 | altitude  NOTE– NE response attribute. | NE | Request for the earliest time or position when the specified altitude can be accepted. |
   | UM149 | CAN YOU ACCEPT (*altitude*) AT (*position*) | A/N | Request to indicate whether or not the specified altitude can be accepted at the specified position. |
   | UM150 | CAN YOU ACCEPT (*altitude*) AT (*time*) | A/N | Request to indicate whether or not the specified altitude can be accepted at the specified time. |
   | UM171 | vertical rate | W/U | Instruction to climb at the specified rate or greater. |
   | UM172 | vertical rate | W/U | Instruction to climb at the specified rate or less. |
   | UM173 | vertical rate | W/U | Instruction to descend at the specified rate or greater. |
   | UM174 | vertical rate | W/U | Instruction to descend at the specified rate or less. |
   | UM175 | altitude | R | Instruction to report when the aircraft has reached the specified altitude.  NOTE– To be interpreted as “Report reaching an assigned altitude.” |
   | UM177 | AT PILOTS DISCRETION | N | Used in conjunction with a clearance or instruction to indicate that the pilot may execute when prepared to do so. |
   | UM180 | REACHING BLOCK (*altitude*) TO (*altitude*)  NOTE– R response attribute. | W/U | Instruction to report upon reaching the specified vertical range. |

   ***TBL 14-3-7*
   Altitude Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM6 | (altitude) | Y | Request to fly at the specified altitude. |
   | DM7 | REQUEST BLOCK (*altitude*) TO (*altitude*) | Y | Request to fly at the specified vertical range. |
   | DM8 | altitude | Y | Request to cruise climb to the specified altitude.  NOTE– Due to different interpretations between the various ATS units, this element should be avoided. |
   | DM9 | (altitude) | Y | Request for a climb to the specified altitude or vertical range. |
   | DM10 | (altitude) | Y | Request for a descent to the specified altitude or vertical range. |
   | DM11 | AT (*position*) REQUEST CLIMB TO (*altitude*) | Y | Request for a climb/descent to the specified altitude to commence at the specified position. |
   | DM12 | AT (*position*) REQUEST DESCENT TO (*altitude*) | Y | Request for a climb/descent to the specified altitude to commence at the specified position. |
   | DM13 | AT TIME (*time*) REQUEST CLIMB TO (*altitude*) | Y | Request for a climb/descent to the specified altitude to commence at the specified time. |  |
   | DM14 | AT TIME (*time*) REQUEST DESCENT TO (*altitude*) | Y | Request for a climb/descent to the specified altitude to commence at the specified time. |
   | DM28 | altitude | N | Notification of leaving the specified altitude. |
   | DM29 | altitude | N | Report indicating climbing to the specified altitude. |
   | DM30 | altitude  NOTE– N alert attribute. | N | Notification of descending to the specified altitude. |
   | DM32 | altitude | N | Notification of the present altitude. |
   | DM37 | altitude | N | Report indicating that the specified altitude is being maintained. |
   | DM38 | (altitude) | N | Confirmation that the assigned altitude is the specified altitude or vertical range. |
   | DM52 | WHEN CAN WE EXPECT LOWER ALTITUDE | Y | Request for the earliest time or position that a descent can be expected. |
   | DM53 | WHEN CAN WE EXPECT HIGHER ALTITUDE | Y | Request for the earliest time or position that a climb can be expected. |
   | DM54 | altitude | Y | Request for the earliest time at which a clearance to cruise climb to the specified altitude can be expected. |
   | DM61 | (altitude)  NOTE– Urgent alert attribute. | N | Report indicating descending to the specified altitude. |
   | DM67 | ‘WE CAN ACCEPT (*altitude*) AT TIME (*time*)’ | N | We can accept the specified altitude at the specified time. |
   | DM67 | ‘WE CANNOT ACCEPT (*altitude*)’ | N | Indication that the specified altitude cannot be accepted. |
   | DM67 | ‘WHEN CAN WE EXPECT CLIMB TO (*altitude*)’ | N | Request for the earliest time at which a clearance to climb to the specified altitude can be expected. |
   | DM67 | ‘WHEN CAN WE EXPECT DESCENT TO (*altitude*)’ | N | Request for the earliest time at which a clearance to descend to the specified altitude can be expected. |
   | DM72 | altitude | N | Notification that the aircraft has reached the specified altitude. |
   | DM75 | AT PILOTS DISCRETION | N | Used in conjunction with another message to indicate that the pilot wishes to execute the request when the pilot is prepared to do so. |
   | DM76 | REACHING BLOCK (*altitude*) TO (*altitude*) | N | Report indicating reaching the specified vertical range. |  |
   | DM77 | ASSIGNED BLOCK (*altitude*) TO (*altitude*)  NOTE– Used for a vertical range. | N | Confirmation that the assigned vertical range is the specified vertical range. |

   ***TBL 14-3-8*
   Crossing Constraint Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM42 | EXPECT TO CROSS (*position*) AT (*altitude*) | R | Notification that a altitude change instruction should be expected which will require the specified position to be crossed at the specified altitude. |
   | UM43 | EXPECT TO CROSS (*position*) AT OR ABOVE (*altitude*) | R | Notification that a altitude change instruction should be expected which will require the specified position to be crossed at or above the specified altitude. |
   | UM44 | EXPECT TO CROSS (*position*) AT OR BELOW (*altitude*) | R | Notification that a altitude change instruction should be expected which will require the specified position to be crossed at or below the specified altitude. |
   | UM45 | EXPECT TO CROSS (*position*) AT AND MAINTAIN (*altitude*) | R | Notification that a altitude change instruction should be expected which will require the specified position to be crossed at the specified altitude which is to be maintained subsequently. |
   | UM46 | CROSS (*position*) AT (*altitude*) | W/U | Instruction that the specified position is to be crossed at the specified altitude. |
   | UM47 | CROSS (*position*) AT OR ABOVE (*altitude*) | W/U | Instruction that the specified position is to be crossed at or above the specified altitude. |
   | UM48 | CROSS (*position*) AT OR BELOW (*altitude*) | W/U | Instruction that the specified position is to be crossed at or below the specified altitude. |
   | UM49 | CROSS (*position*) AT AND MAINTAIN (*altitude*) | W/U | Instruction that the specified position is to be crossed at the specified altitude. |
   | UM50 | CROSS (*position*) BETWEEN (*altitude*) AND (*altitude*) | W/U | Instruction that the specified position is to be crossed within the specified vertical range. |
   | UM51 | CROSS (*position*) AT (*time*) | W/U | Instruction that the specified position is to be crossed at the specified time. |
   | UM52 | CROSS (*position*) AT OR BEFORE (*time*) | W/U | Instruction that the specified position is to be crossed before the specified time. |
   | UM53 | CROSS (*position*) AT OR AFTER (*time*) | W/U | Instruction that the specified position is to be crossed after the specified time. |
   | UM54 | CROSS (*position*) BETWEEN (*time*) AND (*time*) | W/U | Instruction that the specified position is to be crossed between the specified times. |  |
   | UM55 | CROSS (*position*) AT (*speed*) | W/U | Instruction that the specified position is to be crossed at the specified speed. |
   | UM56 | CROSS (*position*) AT OR LESS THAN (*speed*) | W/U | Instruction that the specified position is to be crossed at or less than the specified speed. |
   | UM57 | CROSS (*position*) AT OR GREATER THAN (*speed*) | W/U | Instruction that the specified position is to be crossed at or greater than the specified speed. |
   | UM58 | CROSS (*position*) AT (*time*) AT (*altitude*) | W/U | Instruction that the specified position is to be crossed at the specified time and at the specified altitude. |
   | UM59 | CROSS (*position*) AT OR BEFORE (*time*) AT (*altitude*) | W/U | Instruction that the specified position is to be crossed before the specified time and at the specified altitude. |
   | UM60 | CROSS (*position*) AT OR AFTER (*time*) AT (*altitude*) | W/U | Instruction that the specified position is to be crossed after the specified time and at the specified altitude. |
   | UM61 | CROSS *(position)* AT AND MAINTAIN *(altitude)* AT *(speed)*  NOTE 1. – A vertical range cannot be provided.  NOTE 2. – This message element is equivalent to CSTU-14 plus LVLU-5 in Doc 4444. | W/U | Instruction that the specified position is to be crossed at the altitude specified, and at the specified speed. |
   | UM62 | AT (*time*) CROSS (*position*) AT AND MAINTAIN (*altitude*) | W/U | Instruction that the specified position is to be crossed at the specified time and at the specified altitude. |
   | UM63 | AT (*time*) CROSS (*position*) AT AND MAINTAIN (*altitude*) AT (*speed*) | W/U | Instruction that the specified position is to be crossed at the specified time at the specified altitude, and at the specified speed. |

   ***TBL 14-3-9*
   Speed Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM100 | AT (*time*) EXPECT (*speed*) | R | Notification that a speed instruction may be issued to take effect at the specified time. |
   | UM101 | AT (*position*) EXPECT (*speed*) | R | Notification that a speed instruction may be issued to take effect at the specified position. |
   | UM102 | AT (*altitude*) EXPECT (*speed*) | R | Notification that a speed instruction may be issued to take effect at the specified altitude. |
   | UM103 | AT (*time*) EXPECT (*speed*) TO (*speed*) | R | Notification that a speed range instruction may be issued to be effective at the specified time. |
   | UM104 | AT (*position*) EXPECT (*speed*) TO (*speed*) | R | Notification that a speed range instruction may be issued to be effective at the specified position. |
   | UM105 | AT (*altitude*) EXPECT (*speed*) TO (*speed*) | R | Notification that a speed range instruction may be issued to be effective at the specified altitude. |  |
   | UM106 | speed | W/U | Instruction to maintain the specified speed. |
   | UM107 | MAINTAIN PRESENT SPEED | W/U | Instruction to maintain the present speed. |
   | UM108 | speed | W/U | Instruction to maintain the specified speed or greater. |
   | UM109 | speed | W/U | Instruction to maintain the specified speed or less. |
   | UM110 | MAINTAIN (*speed*) TO (*speed*) | W/U | Instruction to maintain the specified speed range. |
   | UM111 | speed | W/U | Instruction that the present speed is to be increased to the specified speed and maintained until further advised. |
   | UM112 | speed | W/U | Instruction that the present speed is to be increased to the specified speed or greater, and maintained at or above the specified speed until further advised. |
   | UM113 | speed | W/U | Instruction that the present speed is to be reduced to the specified speed and maintained until further advised. |
   | UM114 | speed | W/U | Instruction that the present speed is to be reduced to the specified speed or less, and maintained at or below the specified speed until further advised. |
   | UM115 | speed | W/U | The specified speed is not to be exceeded. |
   | UM116 | RESUME NORMAL SPEED | W/U | Instruction to resume a normal speed. The aircraft no longer needs to comply with a previously issued speed restriction. |
   | UM134 | CONFIRM SPEED  NOTE– NE response attribute. | NE | Request to report the speed defined by the speed type(s). |
   | UM136 | CONFIRM ASSIGNED SPEED  NOTE– NE response attribute. | NE | Request to confirm the assigned speed. |
   | UM151 | speed  NOTE– NE response attribute. | NE | Request for the earliest time or position when the specified speed can be accepted. |

   ***TBL 14-3-10*
   Speed Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM18 | speed | Y | Request for the specified speed. |  |
   | DM19 | REQUEST (*speed*) TO (*speed*) | Y | Request to fly within the specified speed range. |
   | DM34 | speed | N | Report indicating the speed defined by the specified speed types is the specified speed. |
   | DM39 | speed | N | Confirmation that the assigned speed is the specified speed. |
   | DM49 | speed | Y | Request for the earliest time or position that the specified speed can be expected. |
   | DM50 | WHEN CAN WE EXPECT (*speed*) TO (*speed*) | Y | Request for the earliest time at which a clearance to a speed within the specified range can be expected. |
   | DM67 | ‘WE CAN ACCEPT (*speed*) AT TIME (*time*)’ | N | Indication that the specified speed can be accepted at the specified time. |
   | DM67 | ‘WE CANNOT ACCEPT (*speed*)’ | N | Indication that the specified speed cannot be accepted. |

   ***TBL 14-3-11*
   Air Traffic Advisory Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM123 | beacon code | W/U | Instruction to select the specified SSR code. |
   | UM124 | STOP SQUAWK | W/U | Instruction to disable SSR transponder responses. |
   | UM125 | SQUAWK ALTITUDE | W/U | Instruction to include altitude information in the SSR transponder responses. |
   | UM126 | STOP ALTITUDE SQUAWK | W/U | Instruction to stop including altitude information in the SSR transponder responses. |
   | UM144 | CONFIRM SQUAWK  NOTE– NE response attribute. | NE | Request to confirm the selected SSR code. |
   | UM153 | (altimeter)  NOTE– The facility designation and the time of measurement cannot be provided. | R | Advisory providing the specified altimeter setting for the specified facility. |  |
   | UM154 | RADAR SERVICES TERMINATED | R | Advisory that the ATS surveillance service is terminated. |
   | UM155 | position  NOTE– The provision of the position is required. | R | Advisory that ATS surveillance service has been established. A position may be specified position. |
   | UM156 | RADAR CONTACT LOST | R | Advisory that ATS surveillance contact has been lost. |
   | UM158 | ATIS code  NOTE– The airport is not provided. | R | code |
   | UM163 | ICAO facility designation | NE | Notification to the pilot of an ATSU identifier. |
   | UM168 | DISREGARD | N/E | The indicated communication should be ignored.  The previously sent uplink CPDLC message shall be ignored. DISREGARD should not refer to a clearance or instruction. If DISREGARD is used, another element shall be added to clarify which message is to be disregarded. |
   | UM179 | SQUAWK IDENT | W/U | Instruction that the ‘ident’ function on the SSR transponder is to be actuated. |
   | UM182 | CONFIRM ATIS CODE | NE | Instruction to report the identification code of the last ATIS received. |

   ***TBL 14-3-12*
   Air Traffic Advisory Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM47 | code | N | Report indicating that the aircraft is squawking the specified SSR code. |
   | DM79 | ATIS code | N | The code of the latest ATIS received is as specified. |

   ***TBL 14-3-13*
   Voice Communications Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM117 | CONTACT *(ICAO unit name)* *(frequency)* | W/U | Instruction to establish voice contact with the specified ATS unit on the specified frequency. |  |
   | UM118 | AT (*position*) CONTACT (*ICAO unit name*) (*frequency*) | W/U | Instruction at the specified position to establish voice contact with the specified ATS unit on the specified frequency. |
   | UM119 | AT (*time*) CONTACT (*ICAO unit name*) (*frequency*) | W/U | Instruction at the specified time to establish voice contact with the specified ATS unit on the specified frequency. |
   | UM120 | MONITOR *(ICAO unit name)* *(frequency)* | W/U | Instruction to monitor the specified ATS unit on the specified frequency. The flight crew is not required to establish voice contact on the frequency. |
   | UM121 | AT (*position*) MONITOR (*ICAO unit name*) (*frequency*) | W/U | Instruction at the specified position to monitor the specified ATS unit on the specified frequency. The flight crew is not required to establish voice contact on the frequency. |
   | UM122 | AT (*time*) MONITOR (*ICAO unit name*) (*frequency*) | W/U | Instruction at the specified time to monitor the specified ATS unit on the specified frequency. The flight crew is not required to establish voice contact on the frequency. |
   | UM157 | frequency  NOTE– R response attribute. | R | Instruction to check the microphone due to detection of a continuous transmission on the specified frequency. |

   ***TBL 14-3-14*
   Voice Communications Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM20 | REQUEST VOICE CONTACT  NOTE– Used when a frequency is not required. | Y | Request for voice contact on the specified frequency. |
   | DM21 | frequency  NOTE– Used when a frequency is required. | Y | Request for voice contact on the specified frequency. |

   ***TBL 14-3-15*
   Emergency/Urgency Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM131 | REPORT REMAINING FUEL AND SOULS ON BOARD  NOTE– NE response attribute. | Y | Request to provide the fuel remaining (time) and the number of persons on board. |  |
   | UM38  UM39 | Used in combination with LVLU-6 and LVLU-9, which is implemented in FANS 1/A as:  altitude  altitude | N | Instruction to immediately comply with the associated instruction to avoid imminent situation. |

   ***TBL 14-3-16*
   Emergency/Urgency Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM55 | PAN PAN PAN  NOTE– N response attribute. | N | Indication of an urgent situation. |
   | DM56 | MAYDAY MAYDAY MAYDAY  NOTE– N response attribute. | N | Indication of an emergency situation. |
   | DM57 | *(remaining fuel)* OF FUEL REMAINING AND *(remaining souls)* SOULS ON BOARD  NOTE– N response attribute. | N | Report indicating fuel remaining (time) and number of persons on board. |
   | DM58 | CANCEL EMERGENCY  NOTE– N response attribute. | N | Indication that the emergency situation is canceled. |

   ***TBL 14-3-17*
   Standard Response Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM0 | UNABLE | N | Indication that the message cannot be complied with. |
   | UM1 | STANDBY | N | Indication that the message will be responded to shortly. |
   | UM2 | REQUEST DEFERRED | NE | Indication that a long‐term delay in response can be expected. |
   | UM3 | ROGER | N | Indication that the message is received. |  |
   | UM4 | AFFIRM | NE | Indication that ATC is responding positively to the message. |
   | UM5 | NEGATIVE | NE | Indication that ATC is responding negatively to the message. |
   | UM143 | CONFIRM REQUEST | N | Request to confirm the referenced request since the initial request was not understood. The request should be clarified and resubmitted. |

   ***TBL 14-3-18*
   Standard Response Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM0 | WILCO | N | Indication that the instruction is understood and will be complied with. |
   | DM1 | UNABLE | N | Indication that the instruction cannot be complied with. |
   | DM2 | STANDBY | N | Indication that the message will be responded to shortly. |
   | DM3 | ROGER  NOTE– ROGER is the only correct response to an uplink free text message. | N | Indication that the message is received. |
   | DM4 | AFFIRM | N | Indication of a positive response to a message. |
   | DM5 | NEGATIVE | N | Indication of a negative response to a message. |

   ***TBL 14-3-19*
   Supplemental Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM164 | WHEN READY | NE | Indication that the associated instruction is to be executed when the flight crew is ready. |
   | UM165 | THEN | NE | Used to link two messages, indicating the proper order of execution of clearances/ instructions. |
   | UM166 | DUE TO TRAFFIC | N | Indication that the associated message is issued due to the specified reason. |
   | UM167 | DUE TO AIRSPACE RESTRICTION | N | Indication that the associated message is issued due to the specified reason. |
   | UM176 | MAINTAIN OWN SEPARATION AND VMC | W/U | Notification that the pilot is responsible for maintaining separation from other traffic and is also responsible for maintaining Visual Meteorological Conditions. |

   ***TBL 14-3-20*
   Supplemental Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM65 | DUE TO WEATHER | N | Indication that the associated message is issued due to specified reason. |  |
   | DM66 | DUE TO AIRCRAFT PERFORMANCE | N | Indication that the associated message is issued due to specified reason. |

   ***TBL 14-3-21*
   Free Text Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM169 | (free text) | R | A message or part of a message that does not conform to any standard message element in the PANSATM (Doc 4444). |
   | UM169 | (free text) | R | See Note |
   | UM169 | (free text) | R | See Note |
   | UM169 | (free text) | R | See Note |
   | UM169 | (free text) | R | See Note |
   | UM169 | (free text) | R | See Note |
   | UM169 | (free text) | R | See Note |

   NOTE-

   These are FAA scripted free text messages with no GOLD equivalent.

   ***TBL 14-3-22*
   Free Text Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM67 | free text  NOTE– Medium (M) alert attribute. | N |  |
   | DM68 | (free text)  NOTE 1. – Urgency or Medium (M) alert attribute.  NOTE 2. – Selecting any of the emergency message elements will result in this message element being enabled for the flight crew to include in the emergency message at their discretion. | Y |  |

   ***TBL 14-3-23*
   System Management Uplink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM159 | (error information) | N | System‐generated notification of an error. |  |
   | UM160 | (ICAO facility designation)  NOTE– The facility designation is required. | N | System‐generated notification of the next data authority or the cancellation thereof. |
   | UM161 | END SERVICE | NE | Notification to the avionics that the data link connection with the current data authority is being terminated. |
   | UM162 | SERVICE UNAVAILABLE | NE | Notification that the ground system does not support this message. |

   ***TBL 14-3-24*
   System Management Downlink Message Elements**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM62 | (error information) | N | System‐generated notification of an error. |
   | DM63 | NOT CURRENT DATA AUTHORITY | N | System‐generated rejection of any CPDLC message sent from a ground facility that is not the current data authority. |
   | DM64 | (ICAO facility designation)  NOTE– Use by FANS 1/A aircraft in B1 environments. | N | System‐generated notification that the ground system is not designated as the next data authority (NDA), indicating the identity of the current data authority (CDA). Identity of the NDA, if any, is also reported. |

   ***TBL 14-3-25*
   Additional Uplink Messages**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | UM176 | MAINTAIN OWN SEPARATION AND VMC | W/U | Notification that the pilot is responsible for maintaining separation from other traffic and is also responsible for maintaining Visual Meteorological Conditions. |

   ***TBL 14-3-26*
   Additional Downlink Messages**

   |  |  |  |  |
   | --- | --- | --- | --- |
   | **FANS 1/A Message Identifier** | **Message Content** | **Response** | **Message element intended use** |
   | DM74 | REQUEST TO MAINTAIN OWN SEPARATION AND VMC | N | States a desire by the pilot to provide his/her own separation and remain in VMC. |  |
   | DM78 | AT (*time*) (*distance*) (*to/from*) (*position*) | N | At the specified time, the aircraft's position was as specified. |
