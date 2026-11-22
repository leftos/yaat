# USGS National Map imagery as a free source for the YAAT Airport Editor (YAAT-2742)

Researched 2026-11-22 from USGS primary sources and the services' own REST metadata, with live requests at KOAK.

## Answer

USGS National Map imagery is public domain with no usage restrictions, no key and no stated rate limit. USGS asks for a credit line only. It is a licence-clean replacement for keyless Esri World Imagery.

The catch is resolution. The ready-made XYZ tile cache (`USGSImageryOnly`) stops at zoom 16, about 2.4 m per pixel, which is too coarse for taxiway editing. Zoom 17 and 18 at KOAK return HTTP 404.

Airport-scale detail comes from the dynamic image services `USGSNAIPPlus` and `USGSNAIPImagery` on `imagery.nationalmap.gov`. Their pixel size is 0.3 m, and NAIPPlus mixes in 6-inch to 1-foot high-resolution orthoimagery (HRO) for many urban areas. They have no tile cache. A client asks `exportImage` for a Web Mercator bounding box per tile and gets a server-rendered JPEG. That works as an XYZ source if the client computes each tile's bbox. It is slower than a cache, and one of the first test requests returned a 502.

Coverage is US only: CONUS, Hawaii, Alaska, Puerto Rico and territories, with older vintages outside CONUS. Alaska in the cached basemap is 10 m SPOT imagery that is not public domain for download. No SLA or uptime statement was found.

## Services

### USGSImageryOnly (cached basemap)
Source: https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer?f=json
- Tile template (ArcGIS REST, XYZ with row before column): `https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/tile/{z}/{y}/{x}`
- WMTS: `https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/WMTS/1.0.0/WMTSCapabilities.xml`, template `.../WMTS/tile/1.0.0/USGSImageryOnly/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}`
- Web Mercator (wkid 102100 / 3857), 256 px tiles, format MIXED (JPEG/PNG). `tileInfo` lists 24 levels, but `maxScale` is 9027.977 (zoom 16). The description says it is "visible to the 1:9,028 zoom scale".
- Live test at KOAK: `tile/16/25343/10518` returned 200 image/jpeg. `tile/17/50686/21036` and `tile/18/101373/42073` returned 404 ("Error from cloudfront").
- `copyrightText`: "USDA, USGS The National Map: Orthoimagery. Data refreshed June, 2024.v1.2"
- Description: "the majority of the imagery service source is from the National Agriculture Imagery Program (NAIP) for the conterminous United States. The data is 1 meter pixel resolution". Also: "For Alaska, 10-meter resolution SPOT imagery is provided for viewing", "will not be available for direct download ... due to license restrictions". And: "NAIP Data is from 2017-2021 for CONUS, 2013 and 2020 for HI and AK respectively, PR - 2018, and 2012-2013 for the Territories."

### USGSNAIPPlus (dynamic image service, airport scale)
Source: https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer?f=json
- Request: `https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage?bbox={xmin},{ymin},{xmax},{ymax}&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image`
- `pixelSizeX` 0.3 m. Capabilities: Image, Metadata, Catalog, Mensuration. Maximum export size 4000 x 4000. There is no tile cache (`singleFusedMapCache` absent).
- `copyrightText`: "USGS, USDA, The National Map: Orthoimagery. March 12, 2025."
- Description: "consists of National Agriculture Imagery Program (NAIP) and high resolution orthoimagery (HRO)". It says "resolution may vary from 6 inches to 1 meter" and "Many states contribute orthoimagery to The National Map". It also mentions "free downloads of public domain ... orthoimagery ... with many urban areas and other locations at 1-foot (or better) resolution".
- Live test at KOAK: zoom-17 and zoom-18 tile bboxes returned 200 image/jpeg, showing terminal roofs and aircraft. One zoom-18 request returned 502 and the next succeeded.

### USGSNAIPImagery (NAIP only)
Source: https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPImagery/ImageServer?f=json
- Same `exportImage` interface. `pixelSizeX` 0.3 m. "a mosaic primarily of 0.6 meter resolution 4-band imagery presented in natural color", and it supports CIR and NDVI templates.
- `copyrightText`: "USGS, USDA, The National Map: Orthoimagery. January 09, 2025."
- NAIP is flown in leaf-on season on a cycle of three years or less, at 0.6 m (0.3 m optional in coastal states) since 2018 (https://www.usgs.gov/centers/eros/science/usgs-eros-archive-aerial-photography-national-agriculture-imagery-program-naip). That page marks NAIP "Public Domain".

### USDA APFO NAIP services (alternative, not reachable)
`https://gis.apfo.usda.gov/arcgis/rest/services` timed out from this machine. The same NAIP data is served above, so this source was not pursued.

## Licence and usage

- USGS FAQ (https://www.usgs.gov/faqs/what-are-terms-uselicensing-map-services-and-data-national-map): "Map services and data downloaded from The National Map are free and in the public domain. There are no restrictions; however, we request that the following acknowledgment statement ... be included": "Map services and data available from U.S. Geological Survey, National Geospatial Program."
- USGS Copyrights and Credits (https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits): "USGS-authored or produced data and information are considered to be in the U.S. Public Domain". Some non-USGS material is copyrighted and marked as such, which is why the Alaska SPOT layer is excluded from download.
- USGS webmap software release (https://www.usgs.gov/software/webmap-interactive-web-maps-using-national-map-tnm-services): "Map service endpoints are offered with no use restrictions".
- Bulk: the FAQ asks for bulk *vector* downloads by special request (tnm_help@usgs.gov). No rule on bulk tile requests, rate limits or required headers was found, and none limits caching or shipping tiles. Bulk imagery is offered as JPEG 2000 downloads through the TNM download client and EarthExplorer instead.

## Silent or ambiguous

- **Uptime**: no SLA or availability statement found. During testing, one request of four to `exportImage` returned 502, and `gis.apfo.usda.gov` did not answer.
- **Rate limits**: none published. Polite client behaviour (a disk cache, few concurrent requests, a descriptive User-Agent) is self-imposed.
- **Vintage**: NAIP is leaf-on and one to three years old. Airport construction may lag Esri.
- **Non-US airports**: not covered. Esri or another source would still be needed outside the US.

## Sample KOAK tiles for the mock (Web Mercator, 256 px)

- z16 cached: https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/tile/16/25343/10518
- z17 (x 21038, y 50690, runway 12/30 area): https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage?bbox=-13605179.54,4538830.74,-13604873.79,4539136.49&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image
- z18 (x 42077, y 101381): https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage?bbox=-13605026.66,4538830.74,-13604873.79,4538983.61&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image
- z18 (x 42075, y 101369, north field): https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage?bbox=-13605332.41,4540665.23,-13605179.54,4540818.10&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image
- Matching Esri tile: `https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}` with the same z/y/x.
- Bbox per XYZ tile: `s = 40075016.6856 / 2^z`, `xmin = -20037508.3428 + x*s`, `ymax = 20037508.3428 - y*s`, `xmax = xmin + s`, `ymin = ymax - s`.
