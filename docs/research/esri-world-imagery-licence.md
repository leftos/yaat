# Esri World Imagery licence check for the YAAT Airport Editor (YAAT-2515)

Researched 2026-11-22 from Esri primary sources.

## Answer

Esri's terms do not allow a non-Esri desktop app to fetch `services.arcgisonline.com/.../World_Imagery` tiles with no account or key. The service will serve them without a token, but that is a technical fact, not a licence. Esri's FAQ for Esri-owned ArcGIS Online items allows use only "in conjunction with Esri software or an ArcGIS Online subscription". It adds: "If you do not have Esri software, you must purchase an ArcGIS Online subscription". Living Atlas content is also barred from commercial use.

The licensed route for a third-party app is an ArcGIS Location Platform account, which is free to sign up for. Its API key goes to the Basemap Styles or Static Basemap Tiles service (`basemaps-api` / `ibasemaps-api` / `static-map-tiles-api.arcgis.com`), not to `services.arcgisonline.com`. The free tier is 2,000,000 basemap tiles a month, then $0.15 per 1,000; or 1,000 sessions a month, then $4 per 1,000. The account includes a "commercial deployment license" for public apps.

The app must show "Powered by Esri" and the service's data attribution on the map, always visible. Today the World Imagery string is "Esri, Vantor, Earthstar Geographics, and the GIS User Community". Read it from the service's `copyrightText` rather than hard-coding it.

You may not harvest tiles in bulk, redistribute them, self-host them or take them offline except through Esri Content Packages used with Esri software.

The layer is "not intended to be used to export tiles for offline". So the app must not ship tiles, and a disk cache that outlives a session is not covered by any rule found.

Tracing features over the imagery and sharing the vector data is expressly allowed, but the grant names ArcGIS apps. Whether it reaches a non-Esri editor is ambiguous.

Each user fetching tiles live under the app's Location Platform key is the shape Esri prices and licenses. The app shipping tiles is prohibited. Ask Esri in writing about the points that are silent or ambiguous; they are listed in the last section.

## Quoted rules

### Item page licence (World Imagery, item 10df2279f9684e4a9f6a7f08febac2a9)
Source: https://www.arcgis.com/home/item.html?id=10df2279f9684e4a9f6a7f08febac2a9 (the `licenseInfo` field, read via `https://www.arcgis.com/sharing/rest/content/items/10df2279f9684e4a9f6a7f08febac2a9?f=json`)
- "This work is licensed under the Esri Master License Agreement." The "View Terms of Use" link redirects to https://www.esri.com/en-us/legal/terms/master-agreement
- "Export: This layer is not intended to be used to export tiles for offline. If you would like to export imagery for offline use in ArcGIS applications, you may use the World Imagery (for Export) layer"
- "Data Collection and Editing: This layer may be used in various ArcGIS apps to support data collection and editing, with the results used internally or shared with others"
- Item `accessInformation` (credits): "Esri, Vantor, Earthstar Geographics, and the GIS User Community"

### Service metadata
Source: https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer?f=json
- `copyrightText`: "Source: Esri, Vantor, Earthstar Geographics, and the GIS User Community"
- `exportTilesAllowed: false`. The deepest tile level is 23. No rule found caps the zoom level.

### Esri "Frequently asked questions about items owned by Esri in ArcGIS Online" (last updated April 21, 2025)
Source: https://www.esri.com/content/dam/arcgisonline/docs/tou_summary.pdf (the item's "View Summary" link)
- May: "Use items owned by Esri in ArcGIS Online in conjunction with Esri software or an ArcGIS Online subscription for projects and applications that are for internal or external use."
- Offline: "The data may only be taken offline using Esri Content Packages"; "The package can be used with any device as long as it is used exclusively with Esri software."
- Conditions: "Provide attribution to Esri and its data providers." "Use with Esri software and comply with its terms of use. If you do not have Esri software, you must purchase an ArcGIS Online subscription."
- May not: "Systematically harvest basemap tiles through any method other than using Esri Content Packages"; "Redistribute basemap tiles"; "Download, redistribute or self-host any content hosted by Esri."; "Use content from ArcGIS Living Atlas for commercial use in your application or product."
- "If you have an ArcGIS Location Platform subscription or ArcGIS developer bundle: Refer to the ArcGIS Location Platform FAQ"

### World Imagery "Data Collection and Editing Uses Permitted"
Source: https://www.arcgis.com/home/item.html?id=8e90a00a0a6845a49262e0b756f57a10 (PDF)
- "The World Imagery map may be used in various ArcGIS applications to support data collection and editing."
- Item 3: "Esri and its imagery contributors grant Users the non-exclusive right to use the World Imagery map to trace features and validate edits in the creation of vector data. Users that create vector data from the World Imagery map can publicly share that vector data through a GIS data clearinghouse of their own or through another open data site."
- "Except for the additional limited rights granted above, any and all other uses of the World Imagery map remain subject to the terms and conditions set forth in the Esri Master Agreement or Terms of Use"

### Esri developer docs: attribution
Source: https://developers.arcgis.com/documentation/esri-and-data-attribution/
- Esri attribution: the "requirement to display 'Powered by Esri' text in all applications that use Esri technology including ArcGIS Maps SDKs, APIs, content, or services". The text "must be clearly displayed on the map, application, or in a window that is accessible from a menu or button", must "not be covered or obstructed", and must be "easily discoverable". Recommended placement is bottom-right, with "Esri" linked to https://www.esri.com.
- Data attribution: "required regardless of the type of API you are using to access data. For example, it applies to the use of any ArcGIS Maps SDKs, open source library, or third-party API." The rules are "Display names directly on or at the bottom of the map where it is always visible", "Do not obstruct names", and "Frequently verify names as data source providers can change." Names are read from the service's `copyrightText`.

### Esri developer docs and Location Platform: accounts, keys, free tier
- https://developers.arcgis.com/documentation/mapping-and-location-services/faq/ says: "You need ArcGIS Location Platform or ArcGIS Online to build applications with location services."
- https://location.arcgis.com/help/ lists "Commercial deployment license. Deploy public ... and private apps". A public app is one "that allows anonymous access without requiring users to sign in with an ArcGIS account. It supports API key or app authentication." The monthly allowance includes "2,000,000 basemap tiles", "1,000 basemap sessions" and "1,000 static map images". Use must follow "the permitted uses and restrictions described in the ArcGIS Location Platform Agreement."
- https://location.arcgis.com/pricing/ says: "Billing is determined by the number of tile requests to `basemaps-api.arcgis.com`, `ibasemaps-api.arcgis.com`, or `static-map-tiles-api.arcgis.com`." The rate is "2M free then $0.15 per 1,000 tiles", or "1K free then $4 per 1,000 sessions".
- https://developers.arcgis.com/rest/basemap-styles/ says Basemap Styles is "An ArcGIS Location Platform or ArcGIS Online account is required". A basemap session is described as "a single user of a single application can use a session token to access unlimited basemap tiles ... valid for a maximum duration of 12 hours."

## Silent or ambiguous
- **Keyless access**: no source says keyless `services.arcgisonline.com` access is allowed for third-party apps. The FAQ's "Esri software or an ArcGIS Online subscription" condition implies it is not.
- **Imagery style in the free tier**: Location Platform pricing names the `basemaps-api` hosts, not the legacy `services.arcgisonline.com` World_Imagery MapServer. Check in the Basemap Styles / Static Basemap Tiles docs that the imagery style (`arcgis/imagery`) is offered under the key.
- **Disk caching**: no rule addresses a short-lived HTTP cache. The rules found bar "harvest", "download" and "self-host", and allow offline use only through Content Packages with Esri software.
- **Request rate and zoom**: no rule found limits the request rate or the zoom level. Location Platform usage is metered and billed instead.
- **Tracing in a non-Esri app**: the editing grant names ArcGIS apps. Whether tracing taxiways in a non-Esri editor is covered is ambiguous.
- **Living Atlas "commercial use" ban**: a free open-source app is likely not commercial, but the term is undefined.
- **Unread agreements**: the Master Agreement (https://www.esri.com/en-us/legal/terms/master-agreement), the Product-Specific Terms (E300, https://www.esri.com/en-us/legal/terms/product-specific-scope-of-use) and the Location Platform Agreement were not read verbatim for this note. The E300 page returned only its translation index.
