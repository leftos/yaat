# Controller Data Feed

vNAS provides a data feed of current vNAS controller connections including associated primary and secondary position data. The data feed is available for each environment at the following endpoints:

| Environment | Endpoint |
| --- | --- |
| Live | <https://live.env.vnas.vatsim.net/data-feed/controllers.json> |
| Sweatbox 1 | <https://sweatbox1.env.vnas.vatsim.net/data-feed/controllers.json> |
| Sweatbox 2 | <https://sweatbox2.env.vnas.vatsim.net/data-feed/controllers.json> |
| Test | <https://test.virtualnas.net/data-feed/controllers.json> |

> ℹ️ Controller data is updated in real-time whenever there is a change to reflect

The controller data structure is as follows below.

> ℹ️ A C# library is available at <https://github.com/vatsim-vnas/data-feed>

- [`Root`](#root)
- [`Controller`](#controller)
- [`EramPositionData`](#erampositiondata)
- [`Position`](#position)
- [`PositionType`](#positiontype)
- [`Role`](#role)
- [`StarsPositionData`](#starspositiondata)
- [`UserRating`](#userrating)
- [`VatsimData`](#vatsimdata)
- [`VatsimFacilityType`](#vatsimfacilitytype)

## Root

| Property | Type | Notes |
| --- | --- | --- |
| `updatedAt` | `string` | The time the data was generated. ISO 8601 format. |
| `controllers` | [`Controller[]`](#controller) |  |

## Controller

| Property | Type | Notes |
| --- | --- | --- |
| `artccId` | `string` |  |
| `primaryFacilityId` | `string` |  |
| `primaryPositionId` | `string` | The primary position's GUID |
| `role` | [`Role`](#role) |  |
| `positions` | [`Position[]`](#position) |  |
| `isActive` | `bool` |  |
| `isObserver` | `bool` |  |
| `loginTime` | `string` | ISO 8601 format |
| `vatsimData` | [`VatsimData`](#vatsimdata) |  |

## EramPositionData

| Property | Type | Notes |
| --- | --- | --- |
| `sectorId` | `string` |  |

## Position

| Property | Type | Notes |
| --- | --- | --- |
| `facilityId` | `string` |  |
| `facilityName` | `string` |  |
| `positionId` | `string` | The position's GUID |
| `positionName` | `string` |  |
| `positionType` | [`PositionType`](#positiontype) |  |
| `radioName` | `string` |  |
| `defaultCallsign` | `string` | The default VATSIM callsign used at login. Note this may be different from the actual callsign in use (see [`VatsimData.Callsign`](#vatsimdata)). |
| `frequency` | `int` | Expressed in Hz |
| `isPrimary` | `bool` |  |
| `isActive` | `bool` |  |
| `eramData` | [`EramPositionData?`](#erampositiondata) | Nullable |
| `starsData` | [`StarsPositionData?`](#starspositiondata) | Nullable |

> ℹ️ The `positionId` GUID can be used to fetch additional position data from the ARTCC's data set: <https://data-api.vnas.vatsim.net/api/artccs/{ARTCC_ID}>

## PositionType

A `string` enum with the following possible values:

- `Artcc`
- `Tracon`
- `Atct`

## Role

A `string` enum with the following possible values:

- `Observer`
- `Controller`
- `Student`
- `Instructor`

## StarsPositionData

| Property | Type | Notes |
| --- | --- | --- |
| `subset` | `int` |  |
| `sectorId` | `string` |  |
| `areaId` | `string` | The area's GUID |
| `assumedTcps` | `string[]` |  |

> ℹ️ The `areaId` GUID can be used to fetch additional STARS area data from the ARTCC's data set: <https://data-api.vnas.vatsim.net/api/artccs/{ARTCC_ID}>

## UserRating

A `string` enum with the following possible values:

- `Observer`
- `Student1`
- `Student2`
- `Student3`
- `Controller1`
- `Controller2`
- `Controller3`
- `Instructor1`
- `Instructor2`
- `Instructor3`
- `Supervisor`
- `Administrator`

## VatsimData

| Property | Type | Notes |
| --- | --- | --- |
| `cid` | `string` |  |
| `realName` | `string` |  |
| `controllerInfo` | `string` |  |
| `userRating` | [`UserRating`](#userrating) | The user's highest VATSIM rating |
| `requestedRating` | [`UserRating`](#userrating) | The rating the user has logged in as |
| `callsign` | `string` |  |
| `facilityType` | [`VatsimFacilityType`](#vatsimfacilitytype) |  |
| `primaryFrequency` | `int` | Expressed in Hz. Set to `199998000` if inactive. |

## VatsimFacilityType

A `string` enum with the following possible values:

- `Observer`
- `FlightServiceStation`
- `ClearanceDelivery`
- `Ground`
- `Tower`
- `ApproachDeparture`
- `Center`
