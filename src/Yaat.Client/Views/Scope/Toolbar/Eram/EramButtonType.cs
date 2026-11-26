namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// One ERAM toolbar button, of the Master Toolbar or of one of its menus, named after the CRC <c>ButtonType</c> it stands
/// for (CRC has one type for every toolbar button), except the PREFSET items, HIDE TOOLBAR (<see cref="HideToolbar"/>)
/// and the <c>Yaat*</c> values, which are YAAT's own; where a Master Toolbar button sits in the bar is
/// <see cref="EramMasterLayout.Placements"/>, and where a menu's buttons sit is <see cref="EramMenus"/>.
/// </summary>
public enum EramButtonType
{
    /// <summary>CRC's DRAW menu for annotations; YAAT has no feature behind it yet.</summary>
    AnnotationMenu,

    /// <summary>CRC's ATC TOOLS menu.</summary>
    AtcToolsMenu,

    /// <summary>CRC's altitude-block setting menu (AB SETTING); YAAT has no feature behind it yet.</summary>
    SdSettingMenu,

    /// <summary>CRC's range toggle, which steps the display's range.</summary>
    RangeView,

    /// <summary>CRC's CURSOR menu; YAAT has no feature behind it yet.</summary>
    CursorMenu,

    /// <summary>CRC's BRIGHT menu of brightness controls.</summary>
    BrightnessMenu,

    /// <summary>CRC's FONT menu of font sizes.</summary>
    FontSizeMenu,

    /// <summary>CRC's DB FIELDS menu of data-block fields.</summary>
    DbFieldsMenu,

    /// <summary>CRC's increment/decrement button for the length of data-block velocity vectors.</summary>
    VectorLength,

    /// <summary>CRC's VIEWS menu.</summary>
    ViewsMenu,

    /// <summary>CRC's CHECK LISTS menu; YAAT has no feature behind it yet.</summary>
    CheckListsMenu,

    /// <summary>CRC's COMMAND MENUS menu; YAAT has no feature behind it yet.</summary>
    CategoryMenus,

    /// <summary>CRC's GeoMap menu, labelled GEOMAP until the active geomap's two lines replace it.</summary>
    GeomapMenu,

    /// <summary>CRC's altitude limits menu; YAAT has no feature behind it yet.</summary>
    AltitudeLimitsMenu,

    /// <summary>CRC's RADAR FILTER menu.</summary>
    RadarFilterMenu,

    /// <summary>CRC's PREFSET menu.</summary>
    PrefsetMenu,

    /// <summary>CRC's DELETE TEAROFF command.</summary>
    DeleteTearoff,

    /// <summary>YAAT's own HIDE TOOLBAR command in the bar's tenth column, which hides the bar to its SHOW TOOLBAR tile.</summary>
    HideToolbar,

    /// <summary>CRC's VIEWS item CFR; YAAT has no feature behind it yet.</summary>
    CfrView,

    /// <summary>CRC's VIEWS item CRR, the continuous range readout view.</summary>
    CrrView,

    /// <summary>CRC's VIEWS item ALTIM SET; YAAT has no feature behind it yet.</summary>
    AsView,

    /// <summary>CRC's VIEWS item GROUP SUP; YAAT has no feature behind it yet.</summary>
    GroupSupView,

    /// <summary>CRC's VIEWS item CONFLCT ALERT, the conflict alert view.</summary>
    CaView,

    /// <summary>CRC's VIEWS item UA; YAAT has no feature behind it yet.</summary>
    DcrdView,

    /// <summary>CRC's VIEWS item FLIGHT EVENT; YAAT has no feature behind it yet.</summary>
    FlightEventView,

    /// <summary>CRC's VIEWS item CODE; YAAT has no feature behind it yet.</summary>
    CodeView,

    /// <summary>CRC's VIEWS item HOLD LIST; YAAT has no feature behind it yet.</summary>
    HoldViewView,

    /// <summary>CRC's VIEWS item INBND LIST; YAAT has no feature behind it yet.</summary>
    InboundView,

    /// <summary>CRC's VIEWS item DEPT LIST; YAAT has no feature behind it yet.</summary>
    DepartureView,

    /// <summary>CRC's VIEWS item AUTO HO INHIB; YAAT has no feature behind it yet.</summary>
    AhiView,

    /// <summary>CRC's VIEWS item WX REPORT; YAAT has no feature behind it yet.</summary>
    WxView,

    /// <summary>CRC's VIEWS item MRP LIST; YAAT has no feature behind it yet.</summary>
    MrpView,

    /// <summary>CRC's VIEWS item SAA FILTER; YAAT has no feature behind it yet.</summary>
    SaaFilterView,

    /// <summary>CRC's VIEWS item CPDLC MSGOUT; YAAT has no feature behind it yet.</summary>
    MessageOutView,

    /// <summary>CRC's VIEWS item CPDLC HIST; YAAT has no feature behind it yet.</summary>
    MessageHistoryView,

    /// <summary>CRC's VIEWS item CPDLC TOC SET; YAAT has no feature behind it yet.</summary>
    TocSettingsView,

    /// <summary>CRC's VIEWS item CPDLC ADV; YAAT has no feature behind it yet.</summary>
    SavedAdvisoriesView,

    /// <summary>CRC's ATC TOOLS item CRR FIX; YAAT has no feature behind it yet.</summary>
    CrrFix,

    /// <summary>CRC's ATC TOOLS item SPEED ADVSRY; YAAT has no feature behind it yet.</summary>
    SpeedAdvisory,

    /// <summary>CRC's ATC TOOLS item WX, which in YAAT toggles the Scope's one weather display rather than opening CRC's Weather menu.</summary>
    WeatherMenu,

    /// <summary>CRC's GeoMap filter 1 toggle, which YAAT gives the Scope's 1st map toggle.</summary>
    Filter1,

    /// <summary>CRC's GeoMap filter 2 toggle, which YAAT gives the Scope's 2nd map toggle.</summary>
    Filter2,

    /// <summary>CRC's GeoMap filter 3 toggle, which YAAT gives the Scope's 3rd map toggle.</summary>
    Filter3,

    /// <summary>CRC's GeoMap filter 4 toggle, which YAAT gives the Scope's 4th map toggle.</summary>
    Filter4,

    /// <summary>CRC's GeoMap filter 5 toggle, which YAAT gives the Scope's 5th map toggle.</summary>
    Filter5,

    /// <summary>CRC's GeoMap filter 6 toggle, which YAAT gives the Scope's 6th map toggle.</summary>
    Filter6,

    /// <summary>CRC's GeoMap filter 7 toggle, which YAAT gives the Scope's 7th map toggle.</summary>
    Filter7,

    /// <summary>CRC's GeoMap filter 8 toggle, which YAAT gives the Scope's 8th map toggle.</summary>
    Filter8,

    /// <summary>CRC's GeoMap filter 9 toggle, which YAAT gives the Scope's 9th map toggle.</summary>
    Filter9,

    /// <summary>CRC's GeoMap filter 10 toggle, which YAAT gives the Scope's 10th map toggle.</summary>
    Filter10,

    /// <summary>CRC's GeoMap filter 11 toggle, which YAAT gives the Scope's 11th map toggle.</summary>
    Filter11,

    /// <summary>CRC's GeoMap filter 12 toggle, which YAAT gives the Scope's 12th map toggle.</summary>
    Filter12,

    /// <summary>CRC's GeoMap filter 13 toggle, which YAAT gives the Scope's 13th map toggle.</summary>
    Filter13,

    /// <summary>CRC's GeoMap filter 14 toggle, which YAAT gives the Scope's 14th map toggle.</summary>
    Filter14,

    /// <summary>CRC's GeoMap filter 15 toggle, which YAAT gives the Scope's 15th map toggle.</summary>
    Filter15,

    /// <summary>CRC's GeoMap filter 16 toggle, which YAAT gives the Scope's 16th map toggle.</summary>
    Filter16,

    /// <summary>CRC's GeoMap filter 17 toggle, which YAAT gives the Scope's 17th map toggle.</summary>
    Filter17,

    /// <summary>CRC's GeoMap filter 18 toggle, which YAAT gives the Scope's 18th map toggle.</summary>
    Filter18,

    /// <summary>CRC's GeoMap filter 19 toggle, which YAAT gives the Scope's 19th map toggle.</summary>
    Filter19,

    /// <summary>CRC's GeoMap filter 20 toggle, which YAAT gives the Scope's 20th map toggle.</summary>
    Filter20,

    /// <summary>CRC's GeoMap filter 21 toggle, which YAAT gives the Scope's 21st map toggle.</summary>
    Filter21,

    /// <summary>CRC's GeoMap filter 22 toggle, which YAAT gives the Scope's 22nd map toggle.</summary>
    Filter22,

    /// <summary>CRC's GeoMap filter 23 toggle, which YAAT gives the Scope's 23rd map toggle.</summary>
    Filter23,

    /// <summary>CRC's GeoMap filter 24 toggle, which YAAT gives the Scope's 24th map toggle.</summary>
    Filter24,

    /// <summary>CRC's GeoMap filter 25 toggle, which YAAT gives the Scope's 25th map toggle.</summary>
    Filter25,

    /// <summary>CRC's GeoMap filter 26 toggle, which YAAT gives the Scope's 26th map toggle.</summary>
    Filter26,

    /// <summary>CRC's GeoMap filter 27 toggle, which YAAT gives the Scope's 27th map toggle.</summary>
    Filter27,

    /// <summary>CRC's GeoMap filter 28 toggle, which YAAT gives the Scope's 28th map toggle.</summary>
    Filter28,

    /// <summary>CRC's GeoMap filter 29 toggle, which YAAT gives the Scope's 29th map toggle.</summary>
    Filter29,

    /// <summary>CRC's GeoMap filter 30 toggle, which YAAT gives the Scope's 30th map toggle.</summary>
    Filter30,

    /// <summary>CRC's GeoMap filter 31 toggle, which YAAT gives the Scope's 31st map toggle.</summary>
    Filter31,

    /// <summary>CRC's GeoMap filter 32 toggle, which YAAT gives the Scope's 32nd map toggle.</summary>
    Filter32,

    /// <summary>CRC's GeoMap filter 33 toggle, which YAAT gives the Scope's 33rd map toggle.</summary>
    Filter33,

    /// <summary>CRC's GeoMap filter 34 toggle, which YAAT gives the Scope's 34th map toggle.</summary>
    Filter34,

    /// <summary>CRC's GeoMap filter 35 toggle, which YAAT gives the Scope's 35th map toggle.</summary>
    Filter35,

    /// <summary>CRC's GeoMap filter 36 toggle, which YAAT gives the Scope's 36th map toggle.</summary>
    Filter36,

    /// <summary>CRC's GeoMap filter 37 toggle, which YAAT gives the Scope's 37th map toggle.</summary>
    Filter37,

    /// <summary>CRC's GeoMap filter 38 toggle, which YAAT gives the Scope's 38th map toggle.</summary>
    Filter38,

    /// <summary>CRC's GeoMap filter 39 toggle, which YAAT gives the Scope's 39th map toggle.</summary>
    Filter39,

    /// <summary>CRC's GeoMap filter 40 toggle, which YAAT gives the Scope's 40th map toggle.</summary>
    Filter40,

    /// <summary>CRC's BRIGHT item BCKGRD; YAAT has no feature behind it yet.</summary>
    BackgroundBrightness,

    /// <summary>CRC's BRIGHT item CURSOR; YAAT has no feature behind it yet.</summary>
    CursorBrightness,

    /// <summary>CRC's BRIGHT item TEXT; YAAT has no feature behind it yet.</summary>
    TextBrightness,

    /// <summary>CRC's BRIGHT item PR TGT, which steps the Scope's BRITE TARGETS group.</summary>
    PairedTargetBrightness,

    /// <summary>CRC's BRIGHT item UNP TGT, which steps the Scope's BRITE TARGETS group.</summary>
    UnpairedTargetBrightness,

    /// <summary>CRC's BRIGHT item PR HST, which steps the Scope's BRITE HIST group.</summary>
    PairedTargetHistoryBrightness,

    /// <summary>CRC's BRIGHT item UNP HST, which steps the Scope's BRITE HIST group.</summary>
    UnpairedTargetHistoryBrightness,

    /// <summary>CRC's BRIGHT item LDB; YAAT has no feature behind it yet.</summary>
    LdbBrightness,

    /// <summary>CRC's BRIGHT item WX; YAAT has no feature behind it yet.</summary>
    WeatherBrightness,

    /// <summary>CRC's BRIGHT item NEXRAD; YAAT has no feature behind it yet.</summary>
    NexradBrightness,

    /// <summary>CRC's BRIGHT item BCKLGHT; YAAT has no feature behind it yet.</summary>
    BacklightBrightness,

    /// <summary>CRC's BRIGHT item BUTTON; YAAT has no feature behind it yet.</summary>
    ButtonBrightness,

    /// <summary>CRC's BRIGHT item BORDER; YAAT has no feature behind it yet.</summary>
    BorderBrightness,

    /// <summary>CRC's BRIGHT item TOOLBAR; YAAT has no feature behind it yet.</summary>
    ToolbarBrightness,

    /// <summary>CRC's BRIGHT item TB BRDR; YAAT has no feature behind it yet.</summary>
    ToolbarBorderBrightness,

    /// <summary>CRC's BRIGHT item AB BRDR; YAAT has no feature behind it yet.</summary>
    SdBorderBrightness,

    /// <summary>CRC's BRIGHT item FDB, which steps the Scope's BRITE FDB group.</summary>
    FdbBrightness,

    /// <summary>CRC's BRIGHT item SATCOMM; YAAT has no feature behind it yet.</summary>
    SatCommBrightness,

    /// <summary>CRC's BRIGHT item ON-FREQ; YAAT has no feature behind it yet.</summary>
    FdbOnFrequencyBrightness,

    /// <summary>CRC's BRIGHT item FENCE; YAAT has no feature behind it yet.</summary>
    FdbPortalFenceBrightness,

    /// <summary>CRC's BRIGHT item DBFEL; YAAT has no feature behind it yet.</summary>
    FelIndicatorBrightness,

    /// <summary>CRC's BRIGHT item OUTAGE; YAAT has no feature behind it yet.</summary>
    OutageBrightness,

    /// <summary>CRC's BRIGHT item SLDB; YAAT has no feature behind it yet.</summary>
    SelectLdbBrightness,

    /// <summary>CRC's BRIGHT item PORTAL; YAAT has no feature behind it yet.</summary>
    FdbPortalBrightness,

    /// <summary>CRC's BRIGHT item LINE 4, which steps the Scope's BRITE LINE 4 group.</summary>
    FdbLine4Brightness,

    /// <summary>CRC's BRIGHT item DWELL, which steps the Scope's BRITE DWELL group.</summary>
    DwellBrightness,

    /// <summary>CRC's BRIGHT item MAP BRIGHT, which opens the map brightness menu.</summary>
    MapBrightnessMenu,

    /// <summary>CRC's BRIGHT item CPDLC menu; YAAT has no feature behind it yet.</summary>
    CpdlcBrightnessMenu,

    /// <summary>CRC's first map brightness control group, which YAAT draws as MAP A and steps the BRITE MAP A group with.</summary>
    MapBcg1,

    /// <summary>CRC's second map brightness control group, which YAAT draws as MAP B and steps the BRITE MAP B group with.</summary>
    MapBcg2,

    /// <summary>CRC's RADAR FILTER item ALL LDBS; YAAT has no feature behind it yet.</summary>
    AllLdbsFilter,

    /// <summary>CRC's RADAR FILTER item PR LDB; YAAT has no feature behind it yet.</summary>
    PairedLdbsFilter,

    /// <summary>CRC's RADAR FILTER item UNP LDB; YAAT has no feature behind it yet.</summary>
    UnpairedLdbsFilter,

    /// <summary>CRC's RADAR FILTER item ALL PRIM; YAAT has no feature behind it yet.</summary>
    PrimaryTargetFilter,

    /// <summary>CRC's RADAR FILTER item NON MODE-C; YAAT has no feature behind it yet.</summary>
    NonModeCFilter,

    /// <summary>CRC's RADAR FILTER item SELECT BEACON; YAAT has no feature behind it yet.</summary>
    SelectBeaconTargetFilter,

    /// <summary>CRC's RADAR FILTER item PERM ECHO; YAAT has no feature behind it yet.</summary>
    PermanentEchoFilter,

    /// <summary>CRC's RADAR FILTER item STROBE LINES; YAAT has no feature behind it yet.</summary>
    EcmStrobeLinesFilter,

    /// <summary>CRC's RADAR FILTER item HISTORY, which steps the Scope's history count.</summary>
    HistoryFilter,

    /// <summary>CRC's FONT item FDB, which steps the Scope's data-block CHAR size.</summary>
    FdbFontSize,

    /// <summary>CRC's FONT item TOOLBAR; YAAT has no feature behind it yet.</summary>
    ToolbarFontSize,

    /// <summary>CRC's FONT item RDB; YAAT has no feature behind it yet.</summary>
    RdbFontSize,

    /// <summary>CRC's FONT item LDB; YAAT has no feature behind it yet.</summary>
    LdbFontSize,

    /// <summary>CRC's FONT item OUTAGE; YAAT has no feature behind it yet.</summary>
    OutageFontSize,

    /// <summary>CRC's FONT item LINE4; YAAT has no feature behind it yet.</summary>
    Fdb4thLineFontSize,

    /// <summary>CRC's FONT item PORTAL; YAAT has no feature behind it yet.</summary>
    FdbPortalFontSize,

    /// <summary>CRC's DB FIELDS item NON-RVSM; YAAT has no feature behind it yet.</summary>
    NonRvsmIndicatorDisplay,

    /// <summary>CRC's DB FIELDS item DEST, which toggles the Scope's data-block fourth line (it shows destination and type together).</summary>
    DestinationDisplay,

    /// <summary>CRC's DB FIELDS item TYPE, which toggles the Scope's data-block fourth line (it shows destination and type together).</summary>
    AircraftDataDisplay,

    /// <summary>CRC's DB FIELDS item BCAST FLID; YAAT has no feature behind it yet.</summary>
    BcastFlidDisplay,

    /// <summary>CRC's DB FIELDS item PORTAL FENCE; YAAT has no feature behind it yet.</summary>
    FdbPortalFenceDisplay,

    /// <summary>CRC's DB FIELDS item NON-ADSB; YAAT has no feature behind it yet.</summary>
    NonAdsbIndicatorDisplay,

    /// <summary>CRC's DB FIELDS item SAT COMM; YAAT has no feature behind it yet.</summary>
    SatCommDisplay,

    /// <summary>CRC's DB FIELDS item TFM REROUTE; YAAT has no feature behind it yet.</summary>
    TfmRerouteIndicatorDisplay,

    /// <summary>CRC's DB FIELDS item CRR RDB; YAAT has no feature behind it yet.</summary>
    CrrRdbDisplay,

    /// <summary>CRC's DB FIELDS item STA RDB; YAAT has no feature behind it yet.</summary>
    StaRdbDisplay,

    /// <summary>CRC's DB FIELDS item DELAY RDB; YAAT has no feature behind it yet.</summary>
    DelayRdbDisplay,

    /// <summary>CRC's DB FIELDS item VRI; YAAT has no feature behind it yet.</summary>
    VriOverride,

    /// <summary>CRC's DB FIELDS item CODE; YAAT has no feature behind it yet.</summary>
    BeaconCodeDisplayOverride,

    /// <summary>CRC's DB FIELDS item SPEED; YAAT has no feature behind it yet.</summary>
    SpeedDisplayOverride,

    /// <summary>CRC's DB FIELDS item DELAY FORMAT; YAAT has no feature behind it yet.</summary>
    DelayFormatOverride,

    /// <summary>CRC's DB FIELDS item FDB LDR; YAAT has no feature behind it yet.</summary>
    FdbLeaderLength,

    /// <summary>CRC's DB FIELDS item NONADSB; YAAT has no feature behind it yet.</summary>
    NonAdsbBrightness,

    /// <summary>
    /// A PREFSET item that loads one of the Scope's pref sets, its name in the button's key; YAAT's own, as CRC does not
    /// simulate PREFSET.
    /// </summary>
    PrefsetSet,

    /// <summary>The PREFSET item SAVE, which saves the active pref set again; YAAT's own.</summary>
    PrefsetSave,

    /// <summary>The PREFSET item SAVE AS, which types the save-as command into the command box; YAAT's own.</summary>
    PrefsetSaveAs,

    /// <summary>The PREFSET item RENAME, which types the active set's rename into the command box; YAAT's own.</summary>
    PrefsetRename,

    /// <summary>The PREFSET item DELETE, which deletes the active pref set; YAAT's own.</summary>
    PrefsetDelete,

    /// <summary>The YAAT button in the second row's ninth cell, which opens the YAAT-only controls; YAAT's own.</summary>
    YaatMenu,

    /// <summary>The YAAT menu's PROFILE, which opens the profile picks; YAAT's own.</summary>
    YaatProfileMenu,

    /// <summary>The YAAT menu's TEMPLATE, which opens the template picks; YAAT's own.</summary>
    YaatTemplateMenu,

    /// <summary>The YAAT menu's TOOLS, which opens the ring tools and arms the band's kind as the classic TOOLS tray does; YAAT's own.</summary>
    YaatToolsMenu,

    /// <summary>The YAAT menu's DISPLAY, which opens FIX, LOCK, DCNF and OFF CNTR; YAAT's own.</summary>
    YaatDisplayMenu,

    /// <summary>The YAAT menu's DB, which opens the data-block items ERAM's DB FIELDS lacks; YAAT's own.</summary>
    YaatDbMenu,

    /// <summary>The YAAT menu's VIEWS, which opens the views ERAM's VIEWS lacks; YAAT's own.</summary>
    YaatViewsMenu,

    /// <summary>The YAAT menu's PILOT LENS, which opens the lens toggles as the classic PILOT LENS tray does; YAAT's own.</summary>
    YaatLensMenu,

    /// <summary>The YAAT menu's OTHERS, which flips others' level as the classic OTHERS does; YAAT's own.</summary>
    YaatOthers,

    /// <summary>The YAAT menu's AUTO, which unpins the band values as the classic AUTO does; YAAT's own.</summary>
    YaatAuto,

    /// <summary>The PROFILE item STARS, which switches the Scope to the STARS profile; YAAT's own.</summary>
    YaatProfileStars,

    /// <summary>The PROFILE item ERAM, which switches the Scope to the ERAM profile; YAAT's own.</summary>
    YaatProfileEram,

    /// <summary>The PROFILE item MIXED, which switches the Scope to the Mixed profile and its classic bar; YAAT's own.</summary>
    YaatProfileMixed,

    /// <summary>The TEMPLATE item STARS, which picks the STARS template; YAAT's own.</summary>
    YaatTemplateStars,

    /// <summary>The TEMPLATE item ERAM, which picks the ERAM template; YAAT's own.</summary>
    YaatTemplateEram,

    /// <summary>The TEMPLATE item MIXED, which picks the Mixed template; YAAT's own.</summary>
    YaatTemplateMixed,

    /// <summary>The TEMPLATE item EUROSCOPE, which picks the EuroScope template; YAAT's own.</summary>
    YaatTemplateEuroScope,

    /// <summary>The DISPLAY item DCNF, which steps deconfliction to its next mode; YAAT's own.</summary>
    YaatDisplayDcnf,

    /// <summary>The DISPLAY item OFF CNTR, which places the display's centre; YAAT's own.</summary>
    YaatDisplayOffCntr,

    /// <summary>The TOOLS item SIZE, which steps the ring tools' size; YAAT's own.</summary>
    YaatToolSize,

    /// <summary>The YAAT menu's TDM, which toggles top-down mode; YAAT's own.</summary>
    YaatTdm,

    /// <summary>The YAAT menu's MVA, which toggles the MVA map; YAAT's own.</summary>
    YaatMva,

    /// <summary>The TOOLS item J-RING, which arms the J-ring tool; YAAT's own.</summary>
    YaatToolJRing,

    /// <summary>The TOOLS item CONE, which arms the cone tool; YAAT's own.</summary>
    YaatToolCone,

    /// <summary>The TOOLS item DRI 5, which arms the standard DRI tool; YAAT's own.</summary>
    YaatToolDri5,

    /// <summary>The TOOLS item DRI 3, which arms the reduced DRI tool; YAAT's own.</summary>
    YaatToolDri3,

    /// <summary>The DISPLAY item FIX, which toggles the fix display; YAAT's own.</summary>
    YaatDisplayFix,

    /// <summary>The DISPLAY item LOCK, which toggles the display lock; YAAT's own.</summary>
    YaatDisplayLock,

    /// <summary>The DB item DWELL, which toggles hover dwell; YAAT's own.</summary>
    YaatDbDwell,

    /// <summary>The DB item ATPA DIST, which toggles the ATPA distance; YAAT's own.</summary>
    YaatDbAtpa,

    /// <summary>The DB item ALERT CONES, which toggles the ATPA alert cones; YAAT's own.</summary>
    YaatDbAtpaAlertCones,

    /// <summary>The DB item MONITOR CONES, which toggles the ATPA monitor cones; YAAT's own.</summary>
    YaatDbAtpaMonitorCones,

    /// <summary>The DB item CA RING, which toggles the conflict alert ring; YAAT's own.</summary>
    YaatDbCaRing,

    /// <summary>The VIEWS item SSA, which toggles the SSA view; YAAT's own.</summary>
    YaatViewSsa,

    /// <summary>The VIEWS item RPO QUEUE, which toggles the RPO queue view; YAAT's own.</summary>
    YaatViewRpoQueue,

    /// <summary>The PILOT LENS item ON, which toggles the pilot lens; YAAT's own.</summary>
    YaatLensOn,

    /// <summary>The PILOT LENS item PATH, which toggles the lens path; YAAT's own.</summary>
    YaatLensPath,

    /// <summary>The PILOT LENS item QUEUE, which toggles the lens queue; YAAT's own.</summary>
    YaatLensQueue,

    /// <summary>The PILOT LENS item CHECKIN, which toggles the lens check-ins; YAAT's own.</summary>
    YaatLensCheckIn,
}

/// <summary>The two text lines CRC draws on an ERAM toolbar button, and the buttons YAAT has a feature for.</summary>
public static class EramButtonTypeExtensions
{
    /// <summary>
    /// The button's two text lines as CRC draws them, line 1 over line 2, copied from CRC's <c>ButtonTypeExtensions.GetLines</c>;
    /// an empty line draws blank, and GeoMap's is "GEOMAP" until the active geomap's two lines replace it. A GeoMap filter,
    /// a map brightness group and a pref set have blank lines here, as in CRC: their menu gives them their label. The PREFSET
    /// items are YAAT's own labels.
    /// </summary>
    /// <param name="type">The button whose label is asked for.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not an ERAM toolbar button.</exception>
    public static (string Line1, string Line2) Lines(this EramButtonType type) =>
        type switch
        {
            EramButtonType.AnnotationMenu => ("DRAW", ""),
            EramButtonType.AtcToolsMenu => ("ATC", "TOOLS"),
            EramButtonType.SdSettingMenu => ("AB", "SETTING"),
            EramButtonType.RangeView => ("RANGE", ""),
            EramButtonType.CursorMenu => ("CURSOR", ""),
            EramButtonType.BrightnessMenu => ("BRIGHT", ""),
            EramButtonType.FontSizeMenu => ("FONT", ""),
            EramButtonType.DbFieldsMenu => ("DB", "FIELDS"),
            EramButtonType.VectorLength => ("VECTOR", ""),
            EramButtonType.ViewsMenu => ("VIEWS", ""),
            EramButtonType.CheckListsMenu => ("CHECK", "LISTS"),
            EramButtonType.CategoryMenus => ("COMMAND", "MENUS"),
            EramButtonType.GeomapMenu => ("GEOMAP", ""),
            EramButtonType.AltitudeLimitsMenu => ("ALT LIM", ""),
            EramButtonType.RadarFilterMenu => ("RADAR", "FILTER"),
            EramButtonType.PrefsetMenu => ("PREFSET", ""),
            EramButtonType.DeleteTearoff => ("DELETE", "TEAROFF"),
            EramButtonType.HideToolbar => ("HIDE", "TOOLBAR"),
            EramButtonType.CfrView => ("CFR", ""),
            EramButtonType.CrrView => ("CRR", ""),
            EramButtonType.AsView => ("ALTIM", "SET"),
            EramButtonType.GroupSupView => ("GROUP", "SUP"),
            EramButtonType.CaView => ("CONFLCT", "ALERT"),
            EramButtonType.DcrdView => ("UA", ""),
            EramButtonType.FlightEventView => ("FLIGHT", "EVENT"),
            EramButtonType.CodeView => ("CODE", ""),
            EramButtonType.HoldViewView => ("HOLD", "LIST"),
            EramButtonType.InboundView => ("INBND", "LIST"),
            EramButtonType.DepartureView => ("DEPT", "LIST"),
            EramButtonType.AhiView => ("AUTO HO", "INHIB"),
            EramButtonType.WxView => ("WX", "REPORT"),
            EramButtonType.MrpView => ("MRP", "LIST"),
            EramButtonType.SaaFilterView => ("SAA", "FILTER"),
            EramButtonType.MessageOutView => ("CPDLC", "MSGOUT"),
            EramButtonType.MessageHistoryView => ("CPDLC", "HIST"),
            EramButtonType.TocSettingsView => ("CPDLC", "TOC SET"),
            EramButtonType.SavedAdvisoriesView => ("CPDLC", "ADV"),
            EramButtonType.CrrFix => ("CRR", "FIX"),
            EramButtonType.SpeedAdvisory => ("SPEED", "ADVSRY"),
            EramButtonType.WeatherMenu => ("WX", ""),
            EramButtonType.BackgroundBrightness => ("BCKGRD", ""),
            EramButtonType.CursorBrightness => ("CURSOR", ""),
            EramButtonType.TextBrightness => ("TEXT", ""),
            EramButtonType.PairedTargetBrightness => ("PR TGT", ""),
            EramButtonType.UnpairedTargetBrightness => ("UNP TGT", ""),
            EramButtonType.PairedTargetHistoryBrightness => ("PR HST", ""),
            EramButtonType.UnpairedTargetHistoryBrightness => ("UNP HST", ""),
            EramButtonType.LdbBrightness => ("LDB", ""),
            EramButtonType.WeatherBrightness => ("WX", ""),
            EramButtonType.NexradBrightness => ("NEXRAD", ""),
            EramButtonType.BacklightBrightness => ("BCKLGHT", ""),
            EramButtonType.ButtonBrightness => ("BUTTON", ""),
            EramButtonType.BorderBrightness => ("BORDER", ""),
            EramButtonType.ToolbarBrightness => ("TOOLBAR", ""),
            EramButtonType.ToolbarBorderBrightness => ("TB BRDR", ""),
            EramButtonType.SdBorderBrightness => ("AB BRDR", ""),
            EramButtonType.FdbBrightness => ("FDB", ""),
            EramButtonType.SatCommBrightness => ("SATCOMM", ""),
            EramButtonType.FdbOnFrequencyBrightness => ("ON-FREQ", ""),
            EramButtonType.FdbPortalFenceBrightness => ("FENCE", ""),
            EramButtonType.FelIndicatorBrightness => ("DBFEL", ""),
            EramButtonType.OutageBrightness => ("OUTAGE", ""),
            EramButtonType.SelectLdbBrightness => ("SLDB", ""),
            EramButtonType.FdbPortalBrightness => ("PORTAL", ""),
            EramButtonType.FdbLine4Brightness => ("LINE 4", ""),
            EramButtonType.DwellBrightness => ("DWELL", ""),
            EramButtonType.MapBrightnessMenu => ("MAP", "BRIGHT"),
            EramButtonType.CpdlcBrightnessMenu => ("CPDLC", ""),
            EramButtonType.AllLdbsFilter => ("ALL", "LDBS"),
            EramButtonType.PairedLdbsFilter => ("PR", "LDB"),
            EramButtonType.UnpairedLdbsFilter => ("UNP", "LDB"),
            EramButtonType.PrimaryTargetFilter => ("ALL", "PRIM"),
            EramButtonType.NonModeCFilter => ("NON", "MODE-C"),
            EramButtonType.SelectBeaconTargetFilter => ("SELECT", "BEACON"),
            EramButtonType.PermanentEchoFilter => ("PERM", "ECHO"),
            EramButtonType.EcmStrobeLinesFilter => ("STROBE", "LINES"),
            EramButtonType.HistoryFilter => ("HISTORY", ""),
            EramButtonType.FdbFontSize => ("FDB", ""),
            EramButtonType.ToolbarFontSize => ("TOOLBAR", ""),
            EramButtonType.RdbFontSize => ("RDB", ""),
            EramButtonType.LdbFontSize => ("LDB", ""),
            EramButtonType.OutageFontSize => ("OUTAGE", ""),
            EramButtonType.Fdb4thLineFontSize => ("LINE4", ""),
            EramButtonType.FdbPortalFontSize => ("PORTAL", ""),
            EramButtonType.NonRvsmIndicatorDisplay => ("NON-", "RVSM"),
            EramButtonType.DestinationDisplay => ("DEST", ""),
            EramButtonType.AircraftDataDisplay => ("TYPE", ""),
            EramButtonType.BcastFlidDisplay => ("BCAST", "FLID"),
            EramButtonType.FdbPortalFenceDisplay => ("PORTAL", "FENCE"),
            EramButtonType.NonAdsbIndicatorDisplay => ("NON-", "ADSB"),
            EramButtonType.SatCommDisplay => ("SAT", "COMM"),
            EramButtonType.TfmRerouteIndicatorDisplay => ("TFM", "REROUTE"),
            EramButtonType.CrrRdbDisplay => ("CRR", "RDB"),
            EramButtonType.StaRdbDisplay => ("STA", "RDB"),
            EramButtonType.DelayRdbDisplay => ("DELAY", "RDB"),
            EramButtonType.VriOverride => ("VRI", ""),
            EramButtonType.BeaconCodeDisplayOverride => ("CODE", ""),
            EramButtonType.SpeedDisplayOverride => ("SPEED", ""),
            EramButtonType.DelayFormatOverride => ("DELAY", "FORMAT"),
            EramButtonType.FdbLeaderLength => ("FDB LDR", ""),
            EramButtonType.NonAdsbBrightness => ("NONADSB", ""),
            EramButtonType.PrefsetSet => ("", ""),
            EramButtonType.PrefsetSave => ("SAVE", ""),
            EramButtonType.PrefsetSaveAs => ("SAVE", "AS"),
            EramButtonType.PrefsetRename => ("RENAME", ""),
            EramButtonType.PrefsetDelete => ("DELETE", ""),
            EramButtonType.YaatMenu => ("YAAT", ""),
            EramButtonType.YaatProfileMenu => ("PROFILE", ""),
            EramButtonType.YaatTemplateMenu => ("TEMPLATE", ""),
            EramButtonType.YaatToolsMenu => ("TOOLS", ""),
            EramButtonType.YaatDisplayMenu => ("DISPLAY", ""),
            EramButtonType.YaatDbMenu => ("DB", ""),
            EramButtonType.YaatViewsMenu => ("VIEWS", ""),
            EramButtonType.YaatLensMenu => ("PILOT", "LENS"),
            EramButtonType.YaatOthers => ("OTHERS", ""),
            EramButtonType.YaatAuto => ("AUTO", ""),
            EramButtonType.YaatProfileStars => ("STARS", ""),
            EramButtonType.YaatProfileEram => ("ERAM", ""),
            EramButtonType.YaatProfileMixed => ("MIXED", ""),
            EramButtonType.YaatTemplateStars => ("STARS", ""),
            EramButtonType.YaatTemplateEram => ("ERAM", ""),
            EramButtonType.YaatTemplateMixed => ("MIXED", ""),
            EramButtonType.YaatTemplateEuroScope => ("EUROSCOPE", ""),
            EramButtonType.YaatDisplayDcnf => ("DCNF", ""),
            EramButtonType.YaatDisplayOffCntr => ("OFF", "CNTR"),
            EramButtonType.YaatToolSize => ("SIZE", ""),
            EramButtonType.YaatTdm => ("TDM", ""),
            EramButtonType.YaatMva => ("MVA", ""),
            EramButtonType.YaatToolJRing => ("J-RING", ""),
            EramButtonType.YaatToolCone => ("CONE", ""),
            EramButtonType.YaatToolDri5 => ("DRI 5", ""),
            EramButtonType.YaatToolDri3 => ("DRI 3", ""),
            EramButtonType.YaatDisplayFix => ("FIX", ""),
            EramButtonType.YaatDisplayLock => ("LOCK", ""),
            EramButtonType.YaatDbDwell => ("DWELL", ""),
            EramButtonType.YaatDbAtpa => ("ATPA", "DIST"),
            EramButtonType.YaatDbAtpaAlertCones => ("ALERT", "CONES"),
            EramButtonType.YaatDbAtpaMonitorCones => ("MONITOR", "CONES"),
            EramButtonType.YaatDbCaRing => ("CA", "RING"),
            EramButtonType.YaatViewSsa => ("SSA", ""),
            EramButtonType.YaatViewRpoQueue => ("RPO", "QUEUE"),
            EramButtonType.YaatLensOn => ("ON", ""),
            EramButtonType.YaatLensPath => ("PATH", ""),
            EramButtonType.YaatLensQueue => ("QUEUE", ""),
            EramButtonType.YaatLensCheckIn => ("CHECKIN", ""),
            >= EramButtonType.Filter1 and <= EramButtonType.Filter40 => ("", ""),
            EramButtonType.MapBcg1 or EramButtonType.MapBcg2 => ("", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an ERAM toolbar button"),
        };

    /// <summary>
    /// Whether YAAT has a feature behind the button: a Scope feature acts for it. One it has not is drawn dimmed and takes
    /// no input.
    /// </summary>
    /// <param name="type">The button asked about.</param>
    public static bool IsBuilt(this EramButtonType type) =>
        type switch
        {
            EramButtonType.AtcToolsMenu
            or EramButtonType.RangeView
            or EramButtonType.BrightnessMenu
            or EramButtonType.FontSizeMenu
            or EramButtonType.DbFieldsMenu
            or EramButtonType.VectorLength
            or EramButtonType.ViewsMenu
            or EramButtonType.GeomapMenu
            or EramButtonType.RadarFilterMenu
            or EramButtonType.PrefsetMenu
            or EramButtonType.DeleteTearoff
            or EramButtonType.HideToolbar => true,
            EramButtonType.CrrView or EramButtonType.CaView or EramButtonType.WeatherMenu => true,
            EramButtonType.PairedTargetBrightness
            or EramButtonType.UnpairedTargetBrightness
            or EramButtonType.PairedTargetHistoryBrightness
            or EramButtonType.UnpairedTargetHistoryBrightness
            or EramButtonType.FdbBrightness
            or EramButtonType.FdbLine4Brightness
            or EramButtonType.DwellBrightness => true,
            EramButtonType.MapBrightnessMenu or EramButtonType.MapBcg1 or EramButtonType.MapBcg2 => true,
            EramButtonType.FdbFontSize or EramButtonType.DestinationDisplay or EramButtonType.AircraftDataDisplay => true,
            EramButtonType.HistoryFilter => true,
            >= EramButtonType.Filter1 and <= EramButtonType.Filter40 => true,
            >= EramButtonType.PrefsetSet and <= EramButtonType.PrefsetDelete => true,
            >= EramButtonType.YaatMenu and <= EramButtonType.YaatLensCheckIn => true,
            _ => false,
        };
}
