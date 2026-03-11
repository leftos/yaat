# 7110.65 - Chapter 14: Data Link Communications

## Section 1. Terminal Automated Clearances

# Chapter 14. Data Link Communications

## Section 1. Terminal Procedures for Issuing Automated Clearances

#### 14-1-1. PRE‐DEPARTURE CLEARANCE (PDC)

1. PDC must be utilized in accordance with this order and the local facility directive for transmitting automated clearances developed in accordance with FAA Order JO 7210.3, Facility Operation and Administration.
2. Review all clearances for accuracy and route integrity.
3. Ensure all information is complete and understandable to the recipient, and the route of flight is continuous.
4. PDC does not permit amended or revised flight plans to be transmitted. Revised or amended flight plans require the clearance to be verbally issued to the flight crew.

   NOTE-

   A flight plan that initially generates in the tower, with a route assigned by automation, (for example: ADR) is not considered revised or amended and may be transmitted.
5. PDC information must be operational in nature. All selectable fields will be predefined by the Terminal Automation System (TAS) and available from a drop‐down menu.
6. For a minimum of 60 days following the commissioning of a Terminal Data Link System (TDLS), the facility Automatic Terminal Information Service (ATIS) must broadcast that PDC is available.

#### 14-1-2. CONTROLLER PILOT DATA LINK COMMUNICATIONS (CPDLC) – DEPARTURE CLEARANCE (DCL)

1. CPDLC DCL must be utilized in accordance with this order and the local facility directive for transmitting automated clearances developed in accordance with FAA Order JO 7210.3, Facility Operation and Administration.
2. All clearances must be reviewed for accuracy and route integrity. Action must be taken to ensure all information is complete and understandable to the recipient, and the route of flight is continuous.
3. CPDLC permits amended or revised flight plans to be transmitted. Revised or amended flight plans that cannot be delivered using CPDLC must be verbally issued to the flight crew.
4. CPDLC clearance information must be operational in nature. All selectable fields will be predefined by the TAS and available from a drop‐down menu.
5. For a minimum of 60 days following the commissioning of a CPDLC capability, the facility ATIS must broadcast that CPDLC is available.

#### 14-1-3. DEPARTURE CLEARANCE (DCL) APPLICATION (PDC/CPDLC) SELECTABLE FIELDS

1. The DCL application provides up to nine Selectable Fields for the tower controller to enter all other clearance information. Each Selectable Field has a purpose and should only be used for that purpose. For standardization, facilities must use DCL Application Selectable Fields as follows:
   1. Selectable Field 1, SID Field, must contain:
      1. the correctly filed SID, or
      2. the SID assigned by the EAS, or
      3. if No SID is filed or assigned by EAS, the controller must either select a SID or, if no SID is to be assigned, select the “NO SID” option.
   2. Selectable Field 2, Transition Field, is reserved for named Transitions on DPs. Selectable Field 2 must contain:
      1. the correctly filed Transition, or
      2. the Transition assigned by the EAS, or
      3. if No Transition is filed or assigned by EAS, the controller must either select a Transition or, if no Transition is to be assigned, select the “----” option.
   3. Selectable Field 3, Climb Out Field, is reserved for climb related information, such as heading assignments, expected vector assignments, or defined SID climbs. Climb Out Field instructions must never contradict SID instructions and may reiterate pertinent SID information. This field is limited to 32 characters and only those entries adapted by the TAS will be available for selection.
   4. Selectable Field 4, CLIMB VIA Field, is reserved for use when a SID is assigned or selected, and will contain CLIMB VIA SID or CLIMB VIA SID EXCEPT MAINTAIN (altitude) information as follows:
      1. If the assigned SID contains vertical guidance from take‐off to climb to an altitude to maintain, and it is intended that an aircraft vertically navigate in accordance with the SID assigned or entered in Selectable Field 1, then Selectable Field 4 must contain the instruction “CLIMB VIA SID”, or
      2. If the assigned SID does not have an initial altitude to maintain, but contains vertical guidance, and it is intended that an aircraft vertically navigate in accordance with the SID assigned or entered in Selectable Field 1, then Selectable Field 4 must contain the instruction “CLIMB VIA SID EXCEPT MAINTAIN (altitude)”, or
      3. If the assigned altitude is different from the published altitude in the SID, the altitude may be amended using CLIMB VIA SID EXCEPT MAINTAIN (altitude).
   5. Selectable Field 5, Maintain Altitude Field, is reserved for initial altitude Assignment. If no SID is assigned or the assigned SID does not contain either an initial altitude or vertical guidance, then Selectable Field 5 must contain the instruction “MAINTAIN (assigned altitude)”.
   6. Selectable Field 6, Expected Altitude Field, is reserved for specifying when the Expected Altitude would be used in the event of lost communications.
   7. Selectable Field 7, Departure Frequency Field, is reserved for Departure Control Frequency Assignment. The selection of “SEE SID” may be used if the SID contains Departure Control Frequency Assignment specific to the intended departure procedure.
   8. Selectable Field 8, Contact Field, is reserved for additional contact information in accordance with facility directives. This field is limited to 32 characters.
   9. Selectable Field 9, Local Information Field, is reserved for additional information in accordance with facility directives. This field is limited to 34 characters and must not contradict information contained elsewhere in a departure clearance.
